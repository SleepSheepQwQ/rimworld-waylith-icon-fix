# GlyphFix — 技术说明书

| 项 | 值 |
|---|---|
| 组件 | `GlyphFix`（原 `AgentTouchFix`） |
| 目标运行时 | RimWorld 1.7 / Waylith Engine / Android（`com.ports1de.waylith`） |
| 注入方式 | Harmony（运行时，不重打包、不改签名） |
| 补丁点 | `Verse.Widgets.ThingIcon` 的 2 个重载 |
| 稳态开销 | 16.7 ns / 调用（同一 runner 裸 `HashSet.Contains` 为 13.67 ns） |
| 依赖 | `0Harmony`（游戏自带） |

---

## 1. 问题域

移植版把 Unity 替换为 Waylith，渲染链路存在两处与上游 PC 版不一致的行为，表现为"图标空白/图标消失"。二者根因不同，必须分开处理。

### 1.1 缺陷 A — 建造菜单图标空白

```
ThingDef.uiIcon = (Texture2D) def.graphic.MatAt(rot).mainTexture
                                 ▲
                                 └── 解析结果为 CutoutComplexUI 材质，mainTexture 在
                                     本移植版不可读 → uiIcon = BaseContent.BadTex
```

`Widgets.ThingIcon(Rect, ThingDef, …)` 在 `uiIcon == null || uiIcon == BadTex` 时直接 `return`，表现为整格空白。

**判定依据**：`Widgets.cs:531-534`；材质来源 `BuildableDef.cs:304-325`。

### 1.2 缺陷 B — 禁用态图标被默认着色器吞掉

```
Command.GizmoOnGUIInt (disabled)      Command.cs:220-226
   └─ parms.lowLight = true
      └─ Command_VerbTarget.DrawIcon   Command_VerbTarget.cs:45-47
         └─ Widgets.ThingIcon(…, grayscale: true)
            └─ Widgets.cs:504-523  用 ShaderDatabase.GrayscaleGUI 另建材质
               └─ LoadShader("Misc/GrayscaleGUI")
                  └─ ShaderRegistry.Resolve → 未注册 → 回落 Default   ShaderRegistry.cs:62-84
                     └─ 图标以错误程序绘制 → 一片灰、无可辨识内容
```

**关键证据**：本移植版随包 262 个哈希命名的 `shaders/*.spv-cache`，不含 `GrayscaleGUI`。

---

## 2. 解决方案

### 2.1 补丁点选择

只挂在 `Verse.Widgets.ThingIcon`，不触碰 `Verse.Graphic` 的网格提交路径。

> 早期尝试过把 `Graphic.DrawMeshInt` 改道到 `GenDraw.DrawMeshNowOrLater`（即时通道）。该方案在真机上把首次加载从 <1 分钟拖到数分钟——每次绘制都触发 `Material.SetPass`，加载期图标密集时开销被放大。已废弃，并在 CI 中用一条硬断言锁死：`Graphic.DrawMeshInt` 只允许调用 `Graphics.DrawMesh`。

### 2.2 缺陷 A 的处理：绘制期补齐 `uiIcon`

```
Prefix(ThingIcon)
  ├─ 快速路径：实参已在 "已修复" 集合 → return（单次哈希查找）
  ├─ 取 def（Thing 重载则经 def 属性解引用）
  ├─ icon = def.uiIcon
  │    若为空或 BadTex → Fill(def)：
  │       path = graphic.path ?? graphicData.texPath
  │       def.uiIcon = ContentFinder<Texture2D>.Get(path, reportFailure: false)
  ├─ 若 def.uiIconMaterial 名称命中 CutoutComplexUI → 置 null（该材质在本版渲染不出）
  └─ 仅当图标可绘制时才记入缓存；失败不记忆，下一帧重试
```

### 2.3 缺陷 B 的处理：强制 `grayscale = false`

```csharp
// 仅匹配 7 参数、第 2 参为 Thing、第 7 参为 bool 且默认 false 的重载
__args[6] = false;
return true;   // 原方法继续执行：材质、裁剪、GUI.matrix 全归引擎
```

`Widgets.cs:504` 的 `if (grayscale)` 不成立后沿用已解析的正常 UI 材质；禁用态由调用方 `GUI.color`（去饱和 + alpha 0.6）表达，因此"变暗"语义保留，"消失"缺陷消除。

### 2.4 缺陷 C 的处理：实例贴图不可用时的 def 级回退

`Widgets.ThingIcon(Rect, Thing, ...)` 取的就是 `GetIconFor(Thing, ...)` 的返回值，拿到空值直接 `return`：

```
Widgets.cs:489   iconFor = GetIconFor(thing, size, rot, stackOfOne, out material);
Widgets.cs:492   if (iconFor == null || iconFor == BaseContent.BadTex) return;   // 整格不画
```

而 `GetIconFor(Thing, ...)` 是**实例级**的：`thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot).mainTexture`
（`Widgets.cs:739-741`），没有 def 级兜底。带改装/染色/克隆 `Graphic` 的物品（模组换 stuff、磨损重贴图）
可以交出贴图为空的材质，而同一个 def 的 `uiIcon`、`graphic` 却是好的。

处理方式是一个 postfix（两个重载各一个，按参数类型精确识别 `Thing` / `ThingDef` 作为第 0 参，
并要求存在 `out Material material`；4 参数的转发重载不挂）：

```
Postfix(GetIconFor) 仅在 __result 为 null 或 BadTex 时继续
  ├─ ① def.uiIcon 可用（非空且非 BadTex）        → 采用，material = null，color = white
  ├─ ② def.graphic.MatAt(def.defaultPlacingRot).mainTexture 可用 → 同上
  └─ ③ 都不可用 → 原值原样返回
```

`material = null` 是关键：让 `Widgets` 走无材质直绘路径，而不是拿着刚刚失效的材质再画一次。
out 参数通过**运行期闭合的泛型 postfix**（`PostfixThing<TTex, TMat, TCol>`）以 `ref` 方式拿到，
因此本模组仍然不携带任何 `UnityEngine` / `UnityStubs` 引用。

代价：正常路径只有一次 null 判断 + 一次引用比较，无分配；反射（`MatAt`、`mainTexture`）只在命中坏值时执行。
开关：`files/game/agenttouchfix.iconfallback` 内容为 `off` 时整个钩子不安装。

---

## 3. 数据结构与并发

| 结构 | 键 | 值 | 上限 | 语义 |
|---|---|---|---|---|
| `_done` | 实参对象 | — | 4096 | 引用相等（`ReferenceHelpers.GetHashCode`），**不可用 `Def.GetHashCode`** |
| `_names` | 材质对象 | `Material.name` | 512 | 同上；仅用于判定是否摘材质 |
| `_nameProp` | 材质类型 | `PropertyInfo` | — | 元数据一次解析 |

> 为什么必须引用相等：`Verse.Def` 重写了 `GetHashCode() = defNameHash`、`Equals()` 比较 `defNameHash + 类型`（`Def.cs:202-208`）。用默认比较器时，重载后同名的新 `ThingDef` 会被判为"同一个键"，于是新对象命中旧结论 → 图标永久空白。改用引用相等后，新对象天然 miss。

**线程模型**：`ThingIcon` 的全部 45 个调用点位于 UI 层，运行于主循环线程（`FrameRenderer` 的 `mainThreadFeatures`）。实现不依赖该结论：`HashSet` 读写均在主线程分支，慢路径的写操作幂等；非主线程调用只会命中快速路径或直接放过。

---

## 4. 缓存失效

缓存记录的是"这个 def 曾经可绘制"，而引擎会在重建播放数据时把 `uiIcon` 写回 `BadTex`：

```
LanguageDatabase.SelectLanguage → PlayDataLoader.ClearAllPlayData
  → BuildableDef.PostLoad（LongEventHandler 闭包）→ ResolveIcon → uiIcon = BadTex
DebugActionsMisc.HotReloadDefs → PlayDataLoader.HotReloadDefs（同上）
```

因此需要失效机制，否则表现为"切换语言后部分图标永久空白，重启才恢复"。

| 机制 | 触发 | 稳态成本 |
|---|---|---|
| Harmony postfix | `PlayDataLoader.ClearAllPlayData`、`HotReloadDefs` → 清空 `_done` / `_names` | 0 |
| 哨兵探针 | 每 250 ms 一次 `DefDatabase<ThingDef>.GetNamedSilentFail("Steel")`，对象引用变化即清空 | 一次整数比较/调用 |
| 容量上限 | `_done.Count ≥ 4096` 时整表清空重建 | 仅在达到上限时 |

哨兵自身异常时永久关闭自身并记一条 `WARN`，绝不影响绘制路径。钩子安装失败会记录 `invalidation hooks on 0 method(s)`，此时仅依赖哨兵。

---

## 5. 性能预算

| 场景 | ns/call | 说明 |
|---|---|---|
| 裸 `HashSet.Contains`（基线） | 13.67 | 缓存命中的物理下限 |
| 稳态（图标正常、无材质） | **16.7** | 哈希查找 + 一次 `TickCount` 比较 |
| 稳态（图标正常 + 外部材质） | 16.7 | 材质名已缓存 |
| 慢路径（每个 def 一生一次） | 2–30 µs | `ContentFinder.Get` + 解码 |
| 启动期 | 数 ms | Harmony 打 5 个方法 |

按 3 万次/秒折算 ≈ 0.5 ms/s ≈ **0.05 % 单核**。加载阶段 `ThingIcon` 调用数为 0，故不增加加载时间。

---

## 6. 失败模式与观测

| 现象 | 日志 | 处置 |
|---|---|---|
| 钩子未安装 | `invalidation hooks on 0 method(s)` | 依赖哨兵；可检查 `PlayDataLoader` 是否被改名 |
| 哨兵不可用 | `sentinel unavailable` | 同上 |
| 字段解析失败 | `uiIcon field not found on <type>` | 移植版字段改名，需更新反射目标 |
| 缓存被清 | `cache cleared (play data reload \| sentinel changed)` | 正常，每次重载出现一次 |
| 材质被摘 | `dropped icon material '<name>' on <def>` | 正常，最多 5 条 |
| 变体列表形状不明（非"主图+掩码成对"） | `collection: variant shape unexpected, left untouched (n=<count>)` | 正常；无掩码的集合也会出现，最多 5 条 |
| 掩码变体被摘除 | `collection: dropped N mask variant(s) at '<folder>', B -> A` | 正常，每个受影响集合一次 |
| 集合类/反射目标缺失 | `collection: ... skipped` | 移植版改名时出现；其余修复不受影响 |
| 图标回退未安装 | `iconfallback: patched N GetIconFor overload(s)`（N 应为 2） | N<2 时检查 `Widgets.GetIconFor` 是否改名/改签名 |
| 实例贴图不可用、已用 def 级贴图顶上 | `icon fallback: <defName> via uiIcon\|graphic` | 正常，最多 5 条 |
| 图标回退内部异常 | `iconfallback: <msg>`（每个进程一次） | 只 Warn，不影响原绘制 |

日志位置：`<游戏目录>/agenttouchfix.log`（设备上为 `files/game/`）。

### 6.1 诊断模块 testspawn

用来在**用户自己的存档**里一次载入就拿到可判定的证据：同一 def 的武器，某些实例正常、某些空白，说明差异在实例级 `Graphic`。

- **闸门**：只在存档载入路径触发（`Root_Play.LoadedGame` / `Game.LoadGame` / `SavedGameLoaderNow.LoadGameFromSaveFileNow` 依次取第一个可用的），载入后的首 tick 执行。新开局地图不执行。取不到载入回调时整个模块不安装（不会退回 `Map.FinalizeInit`，因为它无法区分读档与开局）。
- **动作**：对每个玩家阵营小人，若装备栏有主武器，就在小人所在格 `ThingMaker.MakeThing(def, stuff)` + `GenSpawn.Spawn` 复制一件到地上；质量（`CompQuality`）与耐久能复制就复制，复制不了只丢该字段、照样生成。小人身上那件不动（不重掷、不替换、不删除）。同一格已有物品堆直接叠。
- **位置回退**：小人脚下不可放置时，按固定罗盘顺序（N/E/S/W 再四角）找第一个可放置的相邻格，并记一行 `test: fallback`；都没有则 `test: skip`。
- **日志**（每件一对，上限各 20 行）：

```
test: origin def=<defName> id=<id> graphic=<类型> tex=<name> size=WxH uiIcon=<name|BadTex> same=<true|false>
test: spawn  def=<defName> id=<id> graphic=<类型> tex=<name> size=WxH uiIcon=<name|BadTex> same=<true|false>
```

`same` = 实例主贴图与 `def.uiIcon` 是否同一对象（两边任一为空一律 false，避免"两边都空"被读成一致）。`origin` 与 `spawn` 字段相同、顺序相同，两行相减就是实例级差异。

失败只记一行 `test: skip <reason>`，全程 try/catch，绝不中断载入。每次载入都会再复制一件（便于反复观察），不做去重。

---

## 7. 构建与验证

```
GitHub Actions（ubuntu + .NET 10）
  ├─ build        编译 mod/Source → AgentTouchFix.zip
  ├─ tests/Bench  1e6 次调用对比旧逻辑与新快速路径，契约断言 11 条
  └─ 产物自检     md5 / 字节数 / About.xml 版本
```

验收标准（真机）：

1. 建造菜单图标非空白
2. 选中已征召小人，征召按钮右侧显示枪械图标且征召前后明暗变化正常
3. 首次进入主界面耗时回到基线（不受模组影响）
4. 切换语言后回建造菜单，图标仍正常，日志出现 `cache cleared (play data reload)`

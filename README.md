# AgentTouchFix — 建造菜单图标修复 / Исправление пустых иконок

RimWorld **1.7 Android（Waylith 移植，`com.ports1de.waylith`）** 的运行时补丁模组。
不修改 APK、不重打包、不重签名，全部通过 Harmony 在运行时打补丁。

Run-time patch mod for the **RimWorld 1.7 Android port (Waylith, `com.ports1de.waylith`)**.
No APK modification, no repacking, no re-signing — everything is applied at run time with Harmony.

> **影响范围 / Scope:** 这是一个**只在部分设备上出现**的问题。多数设备的建造菜单图标正常。
> This is a **device-specific** problem: on most devices the build-menu icons are fine.

---

## 0. 下载 / Скачать

| 方式 | 适合谁 | 链接 |
|---|---|---|
| **Releases（推荐）** | 只想用，不想编译 | [v2.4.0 → `AgentTouchFix-2.4.0.zip`](https://github.com/SleepSheepQwQ/rimworld-waylith-icon-fix/releases/latest) |
| **Actions 产物** | 想拿最新一次构建 | 仓库 `Actions` → `build` → 最新一次运行 → 底部 `Artifacts` → `AgentTouchFix`（需登录 GitHub） |
| **源码构建** | 想改代码 | `dotnet build mod/AgentTouchFix.csproj -c Release`，见第 7 节 |

**推荐路径 / Рекомендуемый путь:** 下载 `AgentTouchFix-2.4.0.zip` → 解压得到 `AgentTouchFix/{About,Assemblies}` →
整个 `AgentTouchFix` 文件夹放进游戏的 `Mods/` → 在「Mod 配置」中启用 → 重启游戏。

Скачайте `AgentTouchFix-2.4.0.zip`, распакуйте, положите папку `AgentTouchFix` в `Mods/`,
включите мод в «Mod config» и перезапустите игру.

**不需要的**：不需要 GitHub 账号（Releases 匿名可下）、不需要 root、不需要改 APK。

---

## 1. 症状 / Симптомы

**中文**

- 建造菜单里**部分**可建造物的图标只剩一个带底色的空框；偶尔闪出 1–2 帧不完整贴图后消失
- 受影响的典型物件：`RoyalBed`（豪华双人床）、`NutrientPasteDispenser`（营养膏制造机）、各类桌子/长凳
- 同批建筑**在地图上渲染正常**，且**没有任何报错/警告/崩溃**，日志和 logcat 都是干净的
- 从第一次启动游戏就可能出现，与是否用过"安全模式"无关

**Русский**

- В меню строительства у **части** объектов вместо иконки остаётся пустая рамка с фоном плитки; иногда на 1–2 кадра мелькает обрезанный фрагмент текстуры
- Типичные примеры: `RoyalBed`, `NutrientPasteDispenser`, различные столы и верстаки
- Сами постройки **на карте рисуются корректно**, при этом **нет ни ошибок, ни предупреждений, ни падений**
- Может проявляться с первого запуска игры и не связано с «безопасным режимом»

---

## 2. 原因 / Причина

**中文**

1. `Verse.BuildableDef.ResolveIcon()`（`BuildableDef.cs:333`）里 `uiIcon = (Texture2D)val.mainTexture`，而该移植层 `Material.mainTexture` **恒为 null** → `uiIcon` 可能永远为空
2. 同一个方法还会生成 `uiIconMaterial`（`CutoutComplexUI_<名字>`）。一旦有材质，`Widgets.ThingIcon` 就走材质路径，而该路径有两种**静默丢弃**：
   - 着色器 `CutoutComplex.frag.glsl` 里 `uv = vUv * _MainTex_ST.zw + _MainTex_ST.xy`（**offset 在 xy、scale 在 zw**，与 Unity 约定相反），参数一旦退化 → 采样到 alpha=0 的角 → `if (f.a < 0.5) discard;` → 整块不画
   - `ImGuiGuiBackend.DrawTextureWithMaterial()` 在 `passMaterial == null` 或 `rect` 退化时直接 `return`，没有任何诊断

**Русский**

1. В `Verse.BuildableDef.ResolveIcon()` (`BuildableDef.cs:333`) выполняется `uiIcon = (Texture2D)val.mainTexture`, а в этом порте `Material.mainTexture` **всегда null** → `uiIcon` может остаться пустым навсегда
2. Тот же метод создаёт `uiIconMaterial` (`CutoutComplexUI_<имя>`). При наличии материала `Widgets.ThingIcon` идёт по «материальному» пути, а там есть два места, где плитка **молча отбрасывается**:
   - шейдер `CutoutComplex.frag.glsl`: `uv = vUv * _MainTex_ST.zw + _MainTex_ST.xy` (**offset в xy, scale в zw** — обратно юнитивскому порядку); при вырожденных параметрах сэмпл попадает в прозрачный угол → `discard` → не рисуется ничего
   - `ImGuiGuiBackend.DrawTextureWithMaterial()` при `passMaterial == null` или вырожденном `rect` просто выходит, без диагностики

相关模组：**[AgentDevTools](https://github.com/SleepSheepQwQ/rimworld-waylith-devtools)** —— 开发者工具的精确点击（输入路径）。两者互不引用，可同时启用。

完整分析（含 CI 量化证据）：[`docs/report-zh-ru.md`](docs/report-zh-ru.md)

---

## 3. 这个模组做了什么 / Что делает мод

**中文** — 在**绘制那一刻**修正两个字段（幂等，字段健康时不做任何事）：

```csharp
// Verse.Widgets.ThingIcon(Rect, ThingDef, …) prefix
1) uiIcon 为空 / BaseContent.BadTex  →  按 graphic.path 用 ContentFinder<Texture2D>.Get 取真实贴图并写回
2) uiIconMaterial 名字以 CutoutComplexUI 开头  →  置空，图标改走 DrawTextureWithTexCoords
```

第 2 条让图标走**与"本来就正常显示的图标"完全同一条路径**（墙、口粮等）。

**Русский** — в момент отрисовки исправляются два поля (идемпотентно):

```csharp
1) uiIcon пуст / BaseContent.BadTex  →  текстура подгружается по graphic.path через ContentFinder<Texture2D>.Get
2) uiIconMaterial начинается с CutoutComplexUI  →  обнуляется, иконка уходит на DrawTextureWithTexCoords
```

Дополнительно мод содержит правки ввода/жестов самого порта (нажатие инструментов разработчика, «прилипание» к объектам, двойной тап, IME, отпускание кнопок при потере фокуса, подавление всплывающих подсказок) — они не относятся к иконкам, но включены, потому что порт без них плохо управляется мышью/тачем.

---

## 4. 安装 / Установка

### 4.1 常规方式（有写入权限时）

把 `AgentTouchFix.zip` 解压到游戏的 `Mods/` 目录，得到 `Mods/AgentTouchFix/{About,Assemblies}`，
然后在游戏内「Mod 配置」启用，重启。

Распакуйте `AgentTouchFix.zip` в `Mods/`, получится `Mods/AgentTouchFix/{About,Assemblies}`, включите мод в «Mod config» и перезапустите игру.

### 4.2 该移植版常用方式（`run-as`，不需要 root）

这个移植版 `debuggable=true`，可以直接写进应用私有目录：

```bash
# 解压出 AgentTouchFix/ 后
cat AgentTouchFix/Assemblies/AgentTouchFix.dll \
  | rish -c 'run-as com.ports1de.waylith sh -c "cat > files/game/Mods/agenttouchfix/Assemblies/AgentTouchFix.dll"'
cat AgentTouchFix/About.xml \
  | rish -c 'run-as com.ports1de.waylith sh -c "cat > files/game/Mods/agenttouchfix/About/About.xml"'
```


> `packageId` 是 **`sleepsheep.waylith.agenttouchfix`**：`ModsConfig.xml` 里 `<li>` 的值必须与它完全一致，
> 否则游戏会在启动时把这个模组静默移出 `activeMods`（表现为"装了但完全没反应"）。

Поле `packageId` — **`sleepsheep.waylith.agenttouchfix`**; значение в `<li>` файла `ModsConfig.xml` должно
совпадать с ним, иначе игра молча удалит мод из `activeMods` при запуске.

> `About.xml` **必须**位于 `<mod>/About/About.xml`，且内容中不能出现未转义的 `<`、`&`
> （否则 `System.Xml.XmlException` → `packageId` 为空 → 整个模组不加载，表现为"补丁完全没生效"）。

Затем включите мод в `ModsConfig.xml` (или через меню «Mod config») и перезапустите игру.

---

## 5. 验证 / Проверка

日志文件：`files/game/agenttouchfix.log`（不是 logcat，这个移植版的 `Log.Message` 不落盘）。

```bash
L=files/game/agenttouchfix.log
grep -a "BUILD v2.6.0"                $L   # 版本已加载
grep -a "drawfix: prefix on"          $L   # 补丁安装成功
grep -a -c "dropped icon material"    $L   # > 0 表示修复真的作用到了图标材质
grep -a "textrace: verified eviction installed" $L   # 验证式驱逐已安装
grep -a "textrace: evict allow="      $L   # 每 30 秒一行汇总（见下）
```

正常输出形如：

```
18:22:06.591 INFO drawfix: prefix on Verse.Widgets.ThingIcon (uiIcon repaired, icon material dropped)
18:22:42.835 INFO drawfix: dropped icon material 'CutoutComplexUI_NutrientDispenser_north' on NutrientPasteDispenser
18:22:42.835 INFO drawfix: dropped icon material 'CutoutComplexUI_TableStonecutter_north' on TableStonecutter
```

| 观察到的现象 | 含义 |
|---|---|
| 三行都有 | 修复在跑；打开建造菜单应看到完整图标 |
| 只有前两行，第三行为 0 | 该设备/该存档本来就没走材质路径，不属于这个 bug |
| 一行都没有 | 模组没加载：检查 `About.xml` 位置与 `ModsConfig.xml` 是否启用 |

纹理驱逐汇总行示例（每 30 秒一行，正常游玩时有这些数字就说明在起作用）：

```
textrace: evict allow=1234 skip{unprovable=37 pinned=890 inflight=12 noval=0 build=0 dirty=0} hard=0 frameReset=0 rssUnknown=0 cooldown=3 proof{hit=1204 real=30 cached=29} rss=1180MiB cap=2900MiB
```

`unprovable > 0` 是关键：这些贴图**本来会被驱逐后永久黑掉**，现在被拦住了。

---

## 6. 没有开关 / Никаких настроек

**这个模组不需要任何配置**，装上就按最合理的方式工作：

| 你可能会想配的东西 | 实际行为（自动） |
|---|---|
| 显存/内存预算 | 取设备物理内存的 45%，夹在 512 MiB – 3 GiB 之间（`/proc/meminfo` 的 `MemTotal`） |
| 哪些贴图该保住 | UI 贴图、≤256 KiB 的小图、最近 300 帧用过的图；其余在"能证明可重载"时照常回收 |
| 与其它模组冲突 | 补丁用 `Priority.Low` 排在别人之后；我们的判定失败一律回退成原版行为 |
| 开关文件 | 没有。只有唯一的应急开关（下表） |

唯一的应急手段：

| 目标 | 做法 |
|---|---|
| 一键停用全部补丁 | 新建 `files/game/agenttouchfix.off`（内容随意），重启游戏 |
| 彻底移除 | 删除 `files/game/Mods/agenttouchfix/` 整个目录（或移除 `ModsConfig.xml` 里的条目） |
| 恢复原版模组列表 | 用 `files/save/Config/ModsConfig.xml.bak-*` 覆盖回去 |

设计理由（为什么不做配置项）：配置项只有在"用户知道该选哪个值"时才有价值；这里的正确答案取决于设备内存与贴图格式，
用户无从判断。给错值 = 又回到"贴图全黑"或"内存被吃光"，比不给开关更糟。

---

## 7. 从源码构建 / Сборка из исходников

需要 .NET 10 SDK。

```bash
dotnet test tests/TexSt/TexSt.csproj -c Release     # 20 条单测（纯逻辑，无需游戏）
dotnet build mod/AgentTouchFix.csproj -c Release    # 产出 AgentTouchFix.dll
```

**默认构建 = 图标修复**（纯反射实现，不需要任何游戏程序集）。
触控/鼠标层的补丁在 `input-layer/`，它们在编译期直接使用游戏的 `Thing`/`IntVec3`/`Vector3` 等类型，
因此需要自备引用程序集，见 `input-layer/README.md`。

编译不依赖任何游戏 DLL：

- `stubs/GameStubs` 提供一个 **`AssemblyName=Assembly-CSharp`** 的最小桩（只有 `Verse.Log` 与
  `[StaticConstructorOnStartup]`），`Private=false`，不会被打包或加载；运行期自然绑定到游戏自带的 `Assembly-CSharp`
- Harmony 走 NuGet `Lib.Harmony`（产物仍是 `0Harmony.dll`，与游戏自带同名）

---

## 8. CI 上的复现（不占设备）/ Воспроизведение в CI

`vk/` 是一个 C 写的离屏渲染器（Vulkan，软件实现 lavapipe），跑**真实**的 `CutoutComplex.frag.glsl`：

```bash
bash vk/run_cases.sh build
```

| 输入 | 期望 |
|---|---|
| `_MainTex_ST=(0,0,1,1)` | 大部分像素被绘制 |
| `_MainTex_ST=(1,1,0,0)`（Unity 顺序） | **0 像素**（复现"空框"） |
| 主贴图全透明 | **0 像素**（绑定自检） |

贴图是 `vk/assets/gen_assets.py` 现场生成的合成图（不透明 + 最后一个 texel 透明，复刻真实贴图形态），
仓库中**不包含任何游戏美术资源**。

---

## 9. 目录结构 / Структура

```
mod/Source/            图标修复（Bootstrap / IconFieldFix / IconPathDecision / Refl）
mod/About/About.xml    模组元数据
input-layer/           可选的触控/鼠标层修复（需要游戏程序集，默认不编译）
stubs/GameStubs/       Assembly-CSharp 编译期桩（不随仓库分发游戏 DLL）
tests/TexSt/           20 条 xunit 单测
vk/                    Vulkan 离屏复现器 + 合成贴图生成脚本
docs/report-zh-ru.md   俄/中双语错误报告（含根因、证据、给上游的建议）
```

---

## 10. 许可与声明 / Лицензия

代码以 **MIT** 发布，见 [`LICENSE`](LICENSE)。
本项目不包含 RimWorld、其 DLC 或该 Android 移植版的任何二进制/美术资源；
使用前请自行合法拥有游戏。与 Ludeon Studios、移植作者均无隶属关系。

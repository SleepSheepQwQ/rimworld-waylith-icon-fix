# 任务交接（给 Codex 或任何接手的人）

## 一句话

本仓库是 **GlyphFix**：一个 Harmony 模组，修复 RimWorld 1.7 安卓移植版 `com.ports1de.waylith`
（Waylith 引擎，1.7.9795）的图标/贴图渲染缺陷。当前发布版 **2.7.9**。

## 现状

已修复并有证据的缺陷（细则见 `docs/DIAGNOSIS.md`、`docs/VERIFICATION.md`）：

1. 建造菜单图标空白 —— `def.uiIcon` 经不可渲染的 `CutoutComplexUI` 材质
2. 征召旁空框 —— `Misc/GrayscaleGUI` shader 在本移植版未注册
3. 带掩码的 DLC 图空白/画成掩码 —— `.png.dds` 双扩展名破坏掩码查找，且 `_m` 进了变体表
4. 部分武器画成暗块 —— 掩码按 `orderby x.name` 排到主体前
5. 方向图 0/4 解析 —— 查找是 ordinal，泄漏布局大小写不一致
6. DX10 DDS 被一律当 BC7、DXT3 被丢弃、DXT1 alpha 丢失 —— `ModDdsLoader` 的格式映射
7. 坏 `.dds` 顶掉同名 `.png` —— `ShadowedByDds`
8. 动物列表图标整类空白 —— `Widgets.GetIconFor(Thing)` 现场合成 `CutoutComplexUI`

门禁（必须全绿才允许写设备）：

| 检查 | 对照 | 装 GlyphFix |
|---|---|---|
| `Graphic_Collection` 变体 | 10 变体 / 5 掩码 | 5 / 0 |
| `Graphic_Multi` 方向 | 0/4 | 4/4 |
| `IbexMale_east` | miss | hit `IbexMALE_east` |
| DXT3 | miss | hit 8×8 |

## 2.7.8

三个残缺陷均已实现（详见 `docs/TASK-2.7.8.md`）：

1. `IconFieldFix` 的 Thing 分支从不执行：`Thing.def` 是公有**字段**，代码却用只找属性的
   `Prop(arg, "def")`
2. `IconFieldFix.Fill` 用 `graphic.path`，而集合的 path 是**目录**；真变体名在
   `subGraphics[i].path`，所以 `ContentFinder.Get(目录)` 必 miss
3. `DdsFormatFix` 只扫描散装 `Textures/**`，`Assets.zip` 内的包通道 DDS 不在范围，BC4/BC5 掩码
   仍被当 BC7 解 → `BadTex`

修复后：loader 前缀负责散装与包通道；全内容 sweep 只重载 unusable DDS；
`ContentFallbackFix` 不再让 `BadTex` 阻止查找前序模组；`AtlasBakeFix` 移除失败贴图留下的透明
tile。最终门禁见 `docs/IMAGE-COMPLETENESS.md` 与 `docs/VERIFICATION.md`。

## 2.7.9

修复建造 Def 的延迟材质缓存：首个 `ThingIcon(ThingDef)` 调用可能早于
`ResolveIcon/PostLoad` 写入 `uiIconMaterial`。旧实现把中间状态记入 `_done`，之后
`CutoutComplexUI` 永远不会被检查。2.7.9 对 ThingDef 路径每次重新检查材质，Thing 实例仍保留
有界缓存，并加入“先绘制、后写入坏材质、再次绘制必须修复”的执行级门禁。

## 硬约束

- **设备操作可恢复**：真机验证前备份 `ModsConfig.xml` 和现有 Mod；不要清理无关数据；GlyphFix
  与 GlyphDiag 的组合必须单独隔离。设备日志、tombstone 和实际加载 DLL hash 属于发布证据
- **零配置**：不加用户开关，失败静默降级
- **不改引擎全局行为**（例如 `Texture2D.name`），只在必要调用点窄口径修补
- **不影响其他模组**：按精确签名打补丁，不做宽泛字符串匹配
- 长耗时探针必须有明确 timeout、可终止并上传原始证据

## 构建与测试

```
dotnet build mod/AgentTouchFix.csproj -c Release
dotnet test  tests/TexSt
```

CI：`.github/workflows/build.yml`；真宿主门禁在配套仓库 `SleepSheepQwQ/rimworld-waylith-re`
（`modcheck.yml`）。

## 事实来源（不要重复推导）

- `docs/DIAGNOSIS.md` —— 逐段诊断链
- `docs/ROOTCAUSE.md` —— 根因
- `docs/REFLECTION-FACTS.md` —— 已验证的反射/成员事实（字段 vs 属性）
- `docs/VERIFICATION.md` —— CI 证据与设备确认
- 配套仓库：`docs/LOADING-FACTS.md`（模组加载语义已证）、`docs/UNIQUE-WEAPON-ICON.md`
  （独特武器链条，含 45237630449 的实测）

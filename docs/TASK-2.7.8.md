# 2.7.8 — 独特武器图标的三个残缺陷

状态（2026-11-24）：三项代码均已实现。散装 DDS、uniqueicon 正例、隔离 ShadowFix 和 Atlas
正例均已通过真宿主门禁；包通道 sweep 已改为 `ModContentPack.GetAllFilesForMod`，并通过
`SweepLoadedContent` 在挂载后强制执行。真实 `Assets.zip` 最终 run 以 `docs/VERIFICATION.md`
为准。

背景与实测：配套仓库 `docs/UNIQUE-WEAPON-ICON.md`（含 CI run 45237630449 的原始输出）。
所有行号指 1.7.9795 反编译：`rimworld-waylith-re/defgraphic/src/Assembly-CSharp.decompiled.cs`。

## 缺陷 1：Thing 分支从不执行（已修）

`IconFieldFix` 用 `Prop(arg, "def")` 解引用实例的 def，但 `Prop` 只走属性解析；
`Verse.Thing.def` 是**公有字段**（`Thing` 类定义处 `public ThingDef def;`）。
结果：装备栏/物品栏里 `uiIcon == BadTex` 的实例永远得不到修复，日志出现
`drawfix: uiIcon field not found on Thing`。

修法：新增字段解析（`Member.Resolve(t, "def", needWrite:false, preferField:true)`），在 Prefix 里替换
`Prop(arg,"def")`。

## 缺陷 2：集合取图用了目录路径（已修）

`IconFieldFix.Fill` 取 `graphic.path`。对 `Graphic_Collection`（含 `Graphic_Random`），
`path` 是**文件夹**（`Graphic_Collection.Init` 里赋值），真实变体名在每个
`subGraphics[i].path` 上（`req.path + "/" + tex.name`）。因此 `ContentFinder.Get(folder)` 必 miss，
`Fill` 返回 false。

修法：先尝试 `subGraphics[i].path`，跳过掩码（复用 `MaskName.IsVariantMask`），取第一个非掩码；
失败再回落到原 `graphic.path`。索引 0 与 `ResolveIcon` 的
`ExtractInnerGraphicFor(null) → SubGraphicFor(null) → subGraphics[0]` 保持一致。

## 缺陷 3：包通道 DDS（已修）

`DdsFormatFix` 通过扫描 `<mod>/Textures/**/*.dds` 建索引（`BuildIndex`），
而 Odyssey/Core 的贴图在 `Assets.zip` 内，`Data/<Dlc>/` 下没有 `Textures/` 目录，
索引为空，`TryRepair` 恒返回 plan 0。结果：包内 BC4/BC5 掩码仍被 `ModDdsLoader` 当 BC7 解，
`LoadRawTextureData` 拒绝 → `BaseContent.BadTex` → `ThingIcon(ThingDef)` 早退。

修法（二选一，优先 A）：
- A. 把修复点从文件系统索引移到 `ModDdsLoader.TryLoadDds`（或其调用点），按 DDS 头里的 DXGI
  真实格式分流；这样散装与包通道同源修复。
- B. 保留索引，改成在 `ContentPack` 加载时按条目名匹配并就地纠正格式（复杂，不推荐）。

实际实现采用 A：`DdsPrefixAlt` patched `TryLoadDds(VirtualFile)`。模块初始化太晚时，
`SweepContent` 不再直接走磁盘目录，而是调用引擎的
`ModContentPack.GetAllFilesForMod(mod, "Textures/", null, null)`，同时枚举散装与 `Assets.zip`
条目；只重载 contentList 中 absent/null/BadTex 的 key，再由同一前缀完成修复。

## 验收（必须全部满足）

1. `dotnet test tests/TexSt` 全绿。
2. 新增 fixture 门禁：`.png.dds` 双扩展名 + DX10 BC4 掩码 + `Graphic_Random` + `CutoutComplex`
   - 断言 `subGraphics.Length == 5`（掩码被 `CollectionFix` 丢弃）
   - 断言 `subGraphics[0]` 名字不以 `_m` 结尾
   - 断言 `def.uiIcon != null && def.uiIcon != BaseContent.BadTex`
   - 断言 `uiIcon.GetPixels32()` 不抛异常，且不透明像素占比 > 0
3. 实机复现（由维护者执行，不由代理触发）：独特武器在装备栏与建造菜单均显示正常图，不再是灰块。
4. 零回归：上文 2.7.7 已修八项的门禁保持全绿。

## 反例控制

把 `VariantPath` 临时改为恒返回 null，以上第 2 条必须重新变红；否则说明测试没有咬住缺陷。

## 额外覆盖

- `DdsPrefixAlt.Owns` 让 `DdsFormatFix` 在 loader 前缀成功后跳过，避免同一 DDS 被解码/上传两次。
- `ContentFallbackFix` 把此后加载模组的 `BadTex` 当作“本 holder 未命中”，允许继续查找前面模组
  的健康同名贴图。
- `DdsPrefixAlt.SweepContent` 的 key 解析和 retry 判定由 `ContentSweepDecision` 单测覆盖。

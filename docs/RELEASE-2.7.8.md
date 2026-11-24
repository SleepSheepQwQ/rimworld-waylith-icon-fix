# GlyphFix 2.7.8 — 玩家验收说明

## 这次优先修复

1. **建造菜单**：图标不再因为 `BadTex` 或 `CutoutComplexUI` 材质变成空框。
2. **殖民者与物品列表**：Thing/Def 两级图标都能回退到实际可绘制的贴图。
3. **动物图标**：修复方向图、双扩展名掩码和动物列表里无法渲染的 UI 材质。
4. **奥德赛特殊武器**：修复 `.png.dds`、DX10 BC4 掩码、`Graphic_Random` 和 `Assets.zip` 包内贴图。

同一套底层修复也处理 DDS 格式、单层 mip、DDS/PNG 同名覆盖、大小写、`BadTex` 和 Atlas
透明 tile。

## 安装

1. 备份当前存档。
2. 把 `GlyphFix.zip` 解压到游戏的 `Mods/`，或运行：

   ```bash
   ./repair-glyphfix.sh --install /你的/RimWorld/Mods
   ```

3. 在游戏的 Mod 列表中启用 **GlyphFix**。
4. 完全重启游戏，而不是只返回主菜单。

脚本不会自动修改 Mod 列表；已有 GlyphFix 默认不会被覆盖。`--force` 会把旧目录放到
`Mods/.glyphfix-backups/`，并做大小写不敏感检测，避免重复安装。

## 本地观察清单

- 打开建造菜单，检查家具、床、营养膏机、武器和工作台图标。
- 打开殖民者与物品列表，检查普通物品和装备中的特殊武器。
- 打开动物/野生动物列表，检查 Ibex、Lynx、Cow、Boomalope 等图标和地图贴图。
- 在奥德赛存档中检查独特左轮及其他 `*_Unique` 武器的装备栏、物品栏和建造/清单图标。
- 切换一次语言或重载存档，再检查图标没有重新变空。

如果仍有空白项，只需提供：屏幕/列表名称、物品英文 `defName`、完整 Mod 列表，以及重启后是否
仍然出现。GlyphFix 不要求上传设备日志。

## 已通过的构建与真宿主检查

- 公共构建 `45314800616`：290 条图标/决策测试、86 条 DDS 测试、Benchmark、脚本语法和打包全绿。
- DDS loader `45309425523`：BC4、DX10 BC3、DXT3、单 mip、DXT1 alpha 修复；旧 5/0、4/4 门禁不回归。
- Atlas `45312477319`：control 2 tiles，修复后 live 保留、dead 移除（1/1）。
- 特殊武器 `45314135780`：真实、plain、`.png.dds`、DX10 BC4 四种布局均为 5/0、DRAW、0 blank。
- 同名 DDS/PNG `45314136663`：control 缺图，修复后返回与 PNG 完全一致的 128×128 像素。

pack 通道的精简 headless 资产包被 harness 自身判为 `Corrupt central directory`，该 run 不作为
通过证据；真实 Odyssey `Assets.zip` 请在本地观察清单中确认。

本地产物：

- `GlyphFix.dll` SHA-256 `d630449f88cfd8d527d7a00d68e71cb10789e8fd5c2e19b99059e282ef406167`
- `GlyphFix.zip` SHA-256 `40271b666f0285bb051d083ef8a0e39dfcd9faab6395e20e64586a5d5b2c893d`

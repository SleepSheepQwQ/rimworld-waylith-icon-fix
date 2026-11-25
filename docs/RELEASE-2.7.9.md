# GlyphFix 2.7.9 — 建造图标延迟材质修复

## 修复重点

2.7.9 修复建造栏图标的生命周期回归：

1. 建造 Def 可能在 `ResolveIcon/PostLoad` 完成之前先被 `ThingIcon(ThingDef)` 观察到。
2. 旧版把这次中间状态缓存为“已修好”。
3. 随后游戏写入不可渲染的 `CutoutComplexUI` 材质，但缓存阻止了再次检查，建筑图标继续缺失或闪烁。
4. 2.7.9 对 ThingDef 路径每次重新检查 `uiIcon` 和 `uiIconMaterial`；Thing 实例路径仍保留有界缓存。

## 回归门禁

发布必须通过：

- “先绘制、后写入 `CutoutComplexUI`、再次绘制必须摘掉材质”的执行级测试；
- 真实 Waylith 托管宿主的建造栏 A/B；
- 动物、独特武器、灰度指令、大小写与集合变体；
- DDS/BC4/BC5/DXT1/DXT3、单 mip 与损坏 DDS + PNG fallback；
- Atlas managed sentinel；
- 软件 Vulkan 像素测试；
- 全部单元测试、benchmark、脚本检查和打包。

## 安装

1. 备份游戏配置和当前 GlyphFix。
2. 把 `GlyphFix.zip` 解压到游戏的 `Mods/`，目录内应包含 `About/` 和 `Assemblies/`。
3. 确认 Mod 列表启用 GlyphFix，并完全重启游戏。
4. 不要同时启用 GlyphDiag；当前诊断 Mod 与修复 Mod 的 GPU 观察路径组合需要在隔离后再次验证。

## 验收

- 打开建造菜单，检查床、营养膏机、工作台和其他 `CutoutComplexUI` 图标。
- 返回主菜单再进入存档，确认图标没有重新变空。
- 检查动物列表、普通武器和独特武器图标。
- 日志应显示 `BUILD 2.7.9.0`；建筑修复发生时还应有 `drawfix: dropped icon material` 或
  `drawfix: uiIcon <-`。

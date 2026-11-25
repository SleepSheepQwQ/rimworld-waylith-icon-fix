# GlyphFix — 环世界 1.7（Waylith）安卓移植版 图标与字形修复

[English](README.md) · **简体中文** · [Русский](README.ru.md)

在运行时修补 Waylith 安卓移植版的图片丢失链，不改 APK、不重打包。

> 曾用名 `AgentTouchFix`，代码相同，换了个更贴切的名字。

## 修了什么

2.7.9 覆盖玩家最常见的四类界面：

- **建造菜单**：`BadTex`、无法渲染的 `CutoutComplexUI`、错误集合路径；
- **殖民者与物品列表**：Thing/Def 两级图标回退，材质坏但贴图仍在的情况；
- **动物图标**：`Graphic_Multi`、方向掩码、双扩展名、Thing 级 UI 材质；
- **奥德赛特殊武器**：`.png.dds`、DX10 BC4 掩码、`Graphic_Random`、Assets.zip 包通道。

底层同时修复 DDS 格式与空洞 mip、DDS 顶掉 PNG、后加载模组的 `BadTex` 遮住前序有效图，
以及静态 Atlas 用透明 tile 替换源图的问题。完整矩阵见 `docs/IMAGE-COMPLETENESS.md`。

2.7.9 另外修复了建造 Def 在材质尚未解析时被过早缓存的问题：即使 `CutoutComplexUI` 在首次
绘制之后才写入，下一次建造菜单绘制仍会重新检查并摘下坏材质。

**一、建造菜单图标空白。** `ThingDef.uiIcon` 在移植版里会变成 `BaseContent.BadTex`——因为图形材质被解析成 `CutoutComplexUI`，而这张材质在本移植版永远拿不到可读贴图。GlyphFix 在绘制时直接从 graphic 的贴图路径补 `uiIcon`，并把渲染不出东西的图标材质摘掉。

**二、征召按钮右边那个空框。** 那其实是武器攻击指令的 gizmo。命令禁用时游戏置 `lowLight = true`，一路传到 `Widgets.ThingIcon(grayscale: true)`，用 `ShaderDatabase.GrayscaleGUI`（`Misc/GrayscaleGUI`）另建材质。移植版只注册了 262 个哈希命名的着色器程序，**没有这一个**，`ShaderRegistry.Resolve` 回落到默认程序，图标就被画成一片灰。GlyphFix 强制 `grayscale = false`，禁用态仍由调用方的 `GUI.color` 表达——图标变暗而不是消失，征召后恢复正常亮度。

## 开销

| | |
|---|---|
| 稳态 | 每次被补丁拦截的调用 **16.7 ns**（同机器裸 `HashSet.Contains` 为 13.67 ns） |
| 合计 | 约 3 万次/秒下 **< 0.1 % 单核** |
| 加载时间 | 不变——进主界面前 `ThingIcon` 一次都不会被调用 |
| 内存 | ≤ 4096 个 def + ≤ 512 个材质名，约 0.3 MB，重载时清空 |

判定缓存会在 `PlayDataLoader.ClearAllPlayData` 与 `HotReloadDefs` 时清空，所以**切换语言不会留下永久空白的图标**；钩子装不上时还有 250 ms 的哨兵兜底。

## 安装

把 `GlyphFix` 放进 `Mods/`，启用，重启。没有其它配置项。

从源码一键构建、测试并打包：

```bash
./repair-glyphfix.sh
```

安装到玩家指定的 Mods 目录：

```bash
./repair-glyphfix.sh --install /你的/RimWorld/Mods
```

脚本不会改 `ModsConfig.xml`，也不会硬编码设备路径。已有 GlyphFix 默认不覆盖；加 `--force`
时会把旧目录移动到 `Mods/.glyphfix-backups/` 下的带时间戳备份。检测忽略大小写，不会同时生成
`glyphfix` 与 `GlyphFix` 两个重复包。

## 环境

环世界 1.7（Waylith 引擎）。针对安卓移植版 `com.ports1de.waylith`，在 Xiaomi 23078RKD5C / Android 14 上验证。

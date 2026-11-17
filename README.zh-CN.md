# GlyphFix — 环世界 1.7（Waylith）安卓移植版 图标与字形修复

[English](README.md) · **简体中文** · [Русский](README.ru.md)

移植版的两个渲染缺陷，两个补丁，不动引擎。

> 曾用名 `AgentTouchFix`，代码相同，换了个更贴切的名字。

## 修了什么

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

## 环境

环世界 1.7（Waylith 引擎）。针对安卓移植版 `com.ports1de.waylith`，在 Xiaomi 23078RKD5C / Android 14 上验证。

# 任务交接（给 Codex 或任何接手的人）

## 一句话

本仓库是 **GlyphFix**：一个 Harmony 模组，修复 RimWorld 1.7 安卓移植版 `com.ports1de.waylith`
（Waylith 引擎，1.7.9795）的图标/贴图渲染缺陷。当前发布版 **2.7.7**。

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

## 当前冲刺：2.7.8

三个已定位、未修的缺陷（详见 `docs/TASK-2.7.8.md`）：

1. `IconFieldFix` 的 Thing 分支从不执行：`Thing.def` 是公有**字段**，代码却用只找属性的
   `Prop(arg, "def")`
2. `IconFieldFix.Fill` 用 `graphic.path`，而集合的 path 是**目录**；真变体名在
   `subGraphics[i].path`，所以 `ContentFinder.Get(目录)` 必 miss
3. `DdsFormatFix` 只扫描散装 `Textures/**`，`Assets.zip` 内的包通道 DDS 不在范围，BC4/BC5 掩码
   仍被当 BC7 解 → `BadTex`

## 硬约束

- **不碰设备**：不启动游戏、不 `am start`/`force-stop`、不读也不索取设备日志；一切验证在 CI 或离线
- **零配置**：不加用户开关，失败静默降级
- **不改引擎全局行为**（例如 `Texture2D.name`），只在必要调用点窄口径修补
- **不影响其他模组**：按精确签名打补丁，不做宽泛字符串匹配
- 探针命令 timeout ≤ 15 秒；长任务放后台并通知

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

# 采样器补丁：把纹理采样钉在 mip 0

> 代码：`mod/Source/SamplerFix.cs`（反射 + Harmony transpiler）、`mod/Source/SamplerIl.cs`（IL 改写，纯逻辑）、`mod/Source/SamplerChoice.cs`（分类表，纯逻辑）。
> 单测：`tests/TexSt/SamplerChoiceTests.cs`、`tests/TexSt/SamplerIlTests.cs`。

## 1. 链路

移植版的 ImGui 后端里只有两个纹理采样器，都建在 `ImGuiVulkanRenderer` 的构造函数中，唯一参数是 `MaxLod`：

```csharp
// ImGuiVulkanRenderer.cs:174-175
_texSampler       = CreateSampler(0f);      // MaxLod 0    -> 只采 mip 0
_texSamplerMipped = CreateSampler(1000f);   // MaxLod 1000 -> 采整条 mip 链
```

`CreateSampler(float maxLod)`（同文件 :1009-1033）其余字段完全相同：`MagFilter = MinFilter = Linear`、
`MipmapMode = Linear`、`AddressMode = Clamp`、`MinLod = 0`、`MaxAnisotropy = 1`。也就是说两个采样器
**只差 mip 区间**，滤除质量、寻址方式、各向异性都一样。

选择哪一个由 `SamplerFor` 决定：

```csharp
// ImGuiVulkanRenderer.cs:875-884
public VkSampler SamplerFor(string? sourcePath)
{
    if (!ImportDefaults.IsOfficialUiTexture(sourcePath)) return _texSamplerMipped;
    return _texSampler;
}
```

而 `ImportDefaults.IsOfficialUiTexture`（`UnityStubs/ImportDefaults.cs:54-61`）是**两个条件**：

```csharp
path.Contains("/Textures/UI/")  // 路径文本
&& IsOfficial(path)             // 且文件位于游戏自己的内容根目录下
```

非官方根目录下的贴图（也就是所有 mod 自带贴图，包括 mod 放在 `Textures/UI/` 下的图标）都过不了第二个
条件，于是拿到 `_texSamplerMipped`：一张只有 1 级、或 mip 链在本移植版里只被填了一部分的图标贴图，
被按 `MaxLod = 1000` 从整条链上采样，缩小绘制时采到的是从未写入的 mip 级 → 噪点/闪烁。

## 2. 补丁做了什么

`SamplerFix.Install()` 在 `[StaticConstructorOnStartup]` 里安装，反射拿到四样东西：

| 目标 | 名字 | 拿不到的后果 |
|---|---|---|
| 类型 | `RimWorldWaylith.Imgui.ImGuiVulkanRenderer` | Warn + 不装 |
| 方法 | `SamplerFor(string)`，要求返回类型名 `VkSampler`、参数是 `string`、是实例方法 | Warn + 不装 |
| 字段 | `_texSampler` | Warn + 不装 |
| 字段 | `_texSamplerMipped`，且与 `_texSampler` 同类型 | Warn + 不装 |

任一步失败都是 **fail-open**：只写一行 `WARN`，不装补丁，游戏和 mod 的其余修复照常。理由很直接——
这只是一个渲染微调，端口升级后字段改名属于可预期事件，不该因此影响加载。

拿得到以后只改一处 IL：把 `SamplerFor` 里对 `_texSamplerMipped` 的 `ldfld` 换成一次调用。

```
   ldarg.0                         ldarg.0
   ldfld _texSamplerMipped    -->  ldarg.1
                                   call object SamplerFix.Pick(object, string)
                                   unbox.any VkSampler
```

`Pick` 返回该实例的 `_texSampler`（按实例缓存在 `ConditionalWeakTable` 里，一次读取，之后不再反射），
`unbox.any` 把装箱的句柄还原成 `VkSampler`。栈形状不变：原来留一个采样器值，现在也留一个。

**为什么是换字段的 IL，而不是 postfix？** 返回值 `VkSampler` 是 Vulkan 句柄结构体，本 mod 在编译期拿不到
这个类型；Harmony 的 `ref __result` 要求类型精确匹配，`ref object` 会装箱、写不回返回值。而 postfix 想改
`_texSamplerMipped` 字段本身也不行：它是 `readonly`。换 `ldfld` 的引用既不需要类型引用，也不会动到
`_texSampler` 那一支（那一支本来就是对的）。

**为什么不按路径判断？** 官方 UI 那一支本来就返回 `_texSampler`，所以"统一成 `_texSampler`"与"只对
`/Textures/UI/` 之外改成 `_texSampler`"在这里**结果完全相同**；而路径判断还要求复刻 `IsOfficial()` 的
内容根测试，从 mod 外部无法可靠做到。按分支改，就不需要复刻那个测试，也就不会漏掉"路径写着 UI、内容根
不是官方"的 mod 图标——那正是 `ImportDefaults.IsOfficialUiTexture` 判 false 的那一类。

改完如果 `ldfld _texSamplerMipped` 一次都没命中（说明端口已经改过这段），立刻 `Unpatch` 并 Warn，不留一
个空 detour。

## 3. 日志

成功路径**只打一行**，在启动流量接近结束（安装后 ≥4 s）后的第一次采样调用时打印一次，之后不再输出：

```
sampler: SamplerFor now returns _texSampler (MaxLod 0) for every texture; the official-UI branch
already did, so only the mipped branch changes. IL sites=1 textures=37 distinct=35
ui_path_on_mipped_branch=3 (one-shot startup summary, no per-texture lines)
```

* `textures` —— 被改写的调用次数（每个新建 descriptor set 一次，不是每帧）；
* `distinct` —— 不同 `sourcePath` 数，上限 4096 条，防止异常运行把集合撑大；
* `ui_path_on_mipped_branch` —— 路径含 `/Textures/UI/` 却仍然走到 mipped 分支的贴图数。这个数大于 0
  就是"路径判断修不好、只有按分支改才对"的实证。

关掉补丁不需要改装配：`files/game/agenttouchfix.mipsampler` 写入 `stock`（或 `off` / `0` / `false`），
重启即回到端口原状。

## 4. 风险：`MaxLod = 0` 对**有完整 mip 链**的贴图是降质

这一条必须写清楚，因为它是这个方案的全部代价。

`MaxLod` 是采样器允许使用的 LOD 上限。钉成 0 之后，无论如何缩小绘制，都只从第 0 级（原分辨率）取样：

* **走样**。缩小到 1/4、1/8 时，GPU 只在原图上取 4 个/8 个样本，像素覆盖不足 → 远处贴图出现噪点、
  摩尔纹（moiré）、移动时闪烁（shimmer）。mip 链存在的意义正是预先算好这些低频版本，`MaxLod = 0` 等于
  把预滤波全部关掉。
* **带宽与缓存压力**。缩小绘制时本可读 1/16 大小的第 4 级，现在必须把原分辨率那一级拉进纹理缓存，
  大贴图（地形、地图图集、长条 atlas）在屏幕上占的像素越少，浪费的带宽占比越高。
* **失去三线性过渡**。`MipmapMode = Linear` 保留着，但两个 LOD 都被夹到 0，级间混合退化为双线性，
  缩放过渡时会有可见的"跳变"。

所以这个补丁**不适合**用在有真实 mip 链、且会被大幅缩小绘制的世界纹理上。

## 5. 为什么对 UI 图标无害

同一件事，在 UI 图标上不成立，有四条独立理由，任何一条都足够：

1. **屏幕空间缩放约等于 1**。ImGui 画图标是把贴图按固定像素尺寸贴到矩形里，1:1 或接近 1:1，`LOD ≈ 0`。
   `MaxLod = 1000` 在这种缩放下本来也只采到第 0 级，改与不改采样结果相同——所以对**官方** UI 贴图这个
   补丁是严格的 no-op（`SamplerChoice.Redirects(官方UI路径, true) == false`，有单测钉住）。
2. **图标不会被透视缩小**。真正的 LOD 需求来自"贴图在屏幕上变小"，UI 没有摄像机距离、没有斜视平面，
   没有那种让 LOD 爬到 3~4 级的几何关系。
3. **这些贴图本来就没有可用的 mip 链**。mod 图标多为单级 PNG/DDS，或在本移植版里 mip 只填了前几级；
   `MaxLod = 1000` 采到的级要么不存在（被夹到末级）要么是未初始化数据——这正是噪点的来源。`MaxLod = 0`
   在这里不是"放弃预滤波"，而是"停止读取从未写入的内存"。
4. **尺寸小**。UI 图标通常 ≤ 128~256 px，即便全分辨率采样，纹理缓存占用也可以忽略，第 4 节里的带宽
   代价在图标上不存在。

一句话：对 UI 图标，"mip 0"就是它本来该有的答案；对远处的大世界纹理，不是。

## 6. 覆盖范围与未做的事

* 只改 `ImGuiVulkanRenderer.SamplerFor(string)`。图标走的是 `TryGetTexRef` → `SamplerFor(sourcePath)`，
  即 UI 绘制路径，已被覆盖。
* `ImGuiMaterialRenderer.SamplerFor(bool mipped, TextureWrapMode wrap)` 未改：它在
  `wrapSamplers` 表里**内联创建**采样器（`MaxLod = mipped ? 1000f : 0f`），没有可换的字段，且材质路径
  与服务图标的那条路径无关。若将来发现材质路径也需要，应当另开一个补丁点，用
  `Ceq/Call` 改写 `mipped` 的实参，而不是复用这里的字段替换。
* 不改 `CreateSampler` / 不改 `_texSamplerMipped`：前者会影响所有纹理，后者是 `readonly` 且被
  descriptor set 引用，改它需要处理重建与销毁顺序，风险远大于收益。

## 7. 验证

本机（Termux / Android）只能验证到"编译通过 + 纯逻辑正确"，Harmony 的 detour 在 bionic 上不可用
（MonoMod 抛 `NotImplementedException`），所以运行时验证放在 CI 与真机：

| 层 | 手段 | 证明什么 |
|---|---|---|
| 分类表 | `SamplerChoiceTests`（6 行理论表 + 4 个事实） | UI / 非 UI / null 各自拿到哪个采样器，以及"官方 UI 不变"这一点 |
| IL 改写 | `SamplerIlTests`（7 个用例，合成函数体） | 命中的 `ldfld` 变成 `ldarg.1 / call Pick / unbox.any`；官方 UI 那一支原样保留；标签与异常块随指令一起搬；没有命中时返回 0 由调用方卸载；同名但不同类的字段不动；静态目标取 `ldarg.0`；引用类型走 `castclass` |
| 装载 | CI 构建 + 真机日志 | `sampler: ...` 一行出现且只出现一次；`IL sites=1`；贴图数符合启动时的绑定量 |

# input-layer（可选，不随默认构建产出）/ optional, not part of the default build

这些补丁修复的是**移植版自身的触控/鼠标层**，与建造菜单图标无关：

| 文件 | 作用 |
|---|---|
| `ToolPatches.cs` | 开发者工具点击（`UI.MouseMapPosition` 钉坐标 + `PortUI.Candidates` 就近吸附）|
| `SnapGeometry.cs` | 对象/地块的屏幕投影与吸附几何 |
| `PointerPatches.cs` | 指针状态修复（失焦释放、ACTION_UP 掩码）|
| `InputReleasePatches.cs` | 鼠标按键/键盘状态在失焦、切后台后的清理 |
| `TextInputPatches.cs` | IME 回字丢失兜底 |
| `PickMode.cs` | 拾取模式（port / exact）|
| `DoubleTapSelect.cs` | 双击建筑全选同类 |
| `TooltipPatches.cs` | 把残留的鼠标悬停提示挪走（`park`）|

**为什么不在默认构建里：** 这些文件在编译期直接使用游戏程序集里的 `Verse.Thing`、`Verse.IntVec3`、
`Verse.Map`、`UnityEngine.Vector3` 等类型。本仓库不附带任何游戏二进制（版权原因），因此默认构建只编译
纯反射实现的图标修复。

## 自行编译完整版

```bash
# 1) 从你合法拥有的游戏安装中取出引用程序集（不提交、不再分发）
#    Assembly-CSharp.dll、0Harmony.dll 以及移植层的 Unity 兼容程序集
mkdir -p game-refs && cp /path/to/Assembly-CSharp.dll game-refs/

# 2) 让 csproj 引用它们
dotnet build mod/AgentTouchFix.csproj -c Release \
  -p:GameRefsDir=$PWD/game-refs -p:IncludeInputLayer=true

# 3) 把 input-layer/Source/*.cs 加进编译
dotnet build mod/AgentTouchFix.csproj -c Release \
  -p:GameRefsDir=$PWD/game-refs -p:IncludeInputLayer=true \
  -p:ExtraCompile="$PWD/input-layer/Source/*.cs"
```

注意：`mod/AgentTouchFix.csproj` 默认只包含 `mod/Source/`，上面的 `ExtraCompile` 仅作示例，
实际使用时请把 `input-layer/Source/*.cs` 复制进 `mod/Source/` 后构建。

# STS2 Local Inspector

只读检查本机 Slay the Spire 2 安装，不复制游戏文件到仓库。

默认自动寻找 D:\SteamLibrary、D:\Steam、标准 Steam 目录，也支持 STS2_DIR。

默认输出到 .local/game-inspection/sts2-local-index.json。

索引关键文件 hash/version、游戏根目录结构，以及真实 sts2.dll 中与以下类别相关的类型和成员：
DevConsole、multiplayer/native action、RNG/seed/shuffle、choice/reward、potion、
card/pile、monster intent/targeting。

运行命令：
dotnet run --project .\tools\Sts2LocalInspector\Sts2LocalInspector.csproj -c Release

指定目录时在命令后传：
-- --game-dir D:\SteamLibrary\steamapps\common\Slay the Spire 2

不得把游戏 DLL、PCK、资源或用户数据提交到 Git。

## 0.107.1 monster move IL evidence

需要审计多人怪物 Move 的 `targets` fanout 时，直接读取 pinned 0.107.1 DLL，不加载游戏程序集：

```powershell
dotnet run --project .\tools\Sts2LocalInspector\Sts2LocalInspector.csproj -c Release -- `
  --game-dir <包含 data_sts2_windows_x86_64 的 pinned game-body 目录> `
  --monster-move-il-output .local\game-inspection\monster-moves-0.107.1.json
```

该模式先校验 `sts2.dll` SHA-256 必须为
`a1f9e653f1e28e4076558fee1e60d218619cb7e057b887c6417f62c62c6d7a52`，
再导出 Monsters 命名空间的 `*Move*` 方法以及 async `<XxxMove>d__*.MoveNext` 状态机 IL。
输出包含 opcode、字符串与可解析的 method/field/type token；只读 PE/.NET metadata，不执行游戏代码。

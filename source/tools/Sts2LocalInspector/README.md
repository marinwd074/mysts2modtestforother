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

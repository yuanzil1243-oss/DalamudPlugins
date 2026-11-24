# NewFolder3 0.7.55.2001 构建源码

基于官方 `0.7.55.20`、提交 `ac90ff5e958c2eb236d5afa380d244d8217ac215`，扩展集中在 `LocalExtensions/`，与旧 `.83` 源码隔离。

功能和维护方法见 [使用与换版说明](docs/使用与换版说明.md)、[扩展壳更新规则](docs/本地扩展壳与更新规则.md)与[验收记录](docs/验收记录.md)。OmenTools 完整源码已随附，直接构建无需初始化子模块。

准备 .NET SDK10.0.401 与 Dalamud API15 开发程序集，在本目录执行：

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.ContractTests -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.SpeedChecks -c Release
```

本目录 `NewFolder3-release.json` 保留官方基底原始文件，仅供源码溯源；本修改版的有效库链为仓库根目录 `pluginmaster.json`。扩展更新应在独立官方 Git 基底工作树操作，不要混入旧归档实现。

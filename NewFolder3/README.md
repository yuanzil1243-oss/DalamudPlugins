# 构建 NewFolder3

这是 0.7.55.39 测试版的公开源码（正式通道保持0.7.55.35）；上层目录的 docs 提供使用、开发及发布说明。

安装 global.json 所指定的 .NET10 SDK，准备国服 Dalamud API15 开发程序集。项目会读取 DALAMUD_HOME 或默认卫月开发库目录。OmenTools 跟踪源码已直接包含，无须初始化子模块。

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.MechanicTests -c Release
dotnet run --project tests/NewFolder3.ContractTests -c Release
dotnet run --project tests/NewFolder3.RestExitTests -c Release
```

采用公开 PublicNoService 能力，不带上游私有服务、私有构建覆盖层、游戏/卫月 SDK 或个人地图缓存。首次构建需从 NuGet 还原开发依赖。

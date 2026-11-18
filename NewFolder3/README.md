# 构建 NewFolder3

这是 0.7.55.41 正式版的公开源码（包含此前.36–.40测试版功能）；上层目录的 docs 提供使用、开发及发布说明。

安装 global.json 所指定的 .NET10 SDK，准备国服 Dalamud API15 开发程序集。项目会读取 DALAMUD_HOME 或默认卫月开发库目录。OmenTools 跟踪源码已直接包含，无须初始化子模块。

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.MechanicTests -c Release
dotnet run --project tests/NewFolder3.ContractTests -c Release
dotnet run --project tests/NewFolder3.RestExitTests -c Release
```

采用公开 PublicRepository/allow-all 能力，仅从本插件仓库读取已签名地图，不启用用户数据上传。不附带上游私有服务、私有构建覆盖层、游戏/卫月 SDK 或个人配置；经授权整理的公共地图位于上层 data/NewFolder3。首次构建需从 NuGet 还原开发依赖。

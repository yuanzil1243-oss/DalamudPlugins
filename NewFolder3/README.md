# NewFolder3 0.7.55.2201 构建源码

基于官方 `0.7.55.22`、提交 `e578aaec1b58945a8bf6868a5c8ec2baae8bc50b`，套用 .2103 扩展壳，完整保留此次官方新增的深层攻略道具策略。修复 AEAssistV3 内部名称识别，并允许内置移速在最终奖励层生效。现有 GCD 保护、跨模式暂停、保档解锁、地图和其他扩展保留，新增业务继续集中在 `LocalExtensions/`；与旧 `.83` 源码隔离。

AE 的加载名称兼容 `AEAssistV3` 与 `AEAssist`，通信仍使用 `AEAssist.CombatRoutine.*` 原端点；不改用户不可读回的 `/aestop` 锁，AE 仍不参与 GCD 补打。移速仅在 FSD 运行且普通层／最终奖励层有效可步行时启用；Boss、过场、停止和副本外撤销。按用户要求，通过构建和离线检查后直接发布，不等待游戏内试玩；新行为不标为实战通过。

功能和维护方法见 [使用与换版说明](docs/使用与换版说明.md)、[扩展壳更新规则](docs/本地扩展壳与更新规则.md)与[验收记录](docs/验收记录.md)。OmenTools 完整源码已随附，直接构建无需初始化子模块。

准备 .NET SDK10.0.401 与 Dalamud API15 开发程序集，在本目录执行：

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.ContractTests -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.SpeedChecks -c Release
```

本目录 `NewFolder3-release.json` 保留官方基底原始文件，仅供源码溯源；本修改版的有效库链为仓库根目录 `pluginmaster.json`。扩展更新应在独立官方 Git 基底工作树操作，不要混入旧归档实现。

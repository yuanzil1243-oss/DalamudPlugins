# NewFolder3 0.7.55.2103 构建源码

基于官方 `0.7.55.21`、提交 `a71af60a01682eb76cd6d1ec46ac971b88f75d5a`，沿用 .2102 扩展壳。新增默认关闭的 GCD 停滞保护，补全各运行模式本组出本后暂停，并可在副本外解除续跑锁定、保留游戏存档。扩展集中在 `LocalExtensions/`，与旧 `.83` 源码隔离；官方战斗、探索、道具、机制和奖励实现保留，仅增加受控调度接入和可选的单次补打。安装包、源码及扩展壳按原库链发布。

GCD 保护只在同一 FSD 批准目标连续满足输出条件 6 秒后尝试一次基础 GCD，支持状态可读回的 RSR / Wrath / Prome。AE、Custom、INH3 不补打；Boss 模块 Faulted 时保留禁攻保护。本功能不等于修复 `BossMod.ActionDefinitions` 初始化故障。新功能仍待游戏内实战验证。

功能和维护方法见 [使用与换版说明](docs/使用与换版说明.md)、[扩展壳更新规则](docs/本地扩展壳与更新规则.md)与[验收记录](docs/验收记录.md)。OmenTools 完整源码已随附，直接构建无需初始化子模块。

准备 .NET SDK10.0.401 与 Dalamud API15 开发程序集，在本目录执行：

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.ContractTests -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.SpeedChecks -c Release
```

本目录 `NewFolder3-release.json` 保留官方基底原始文件，仅供源码溯源；本修改版的有效库链为仓库根目录 `pluginmaster.json`。扩展更新应在独立官方 Git 基底工作树操作，不要混入旧归档实现。

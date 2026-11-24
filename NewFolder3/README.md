# NewFolder3 0.7.55.2203 构建源码

本版修复自动输出诊断可能抛异常、进而触发主循环移速保护的问题。AE 与自定义指令的控制记录不再查询安装清单或版本，只保留原接口／指令调用结果；可选诊断出错不影响输出控制结果。真实主循环异常仍撤销移速。本地 AE 安装不要求在线库来源或版本字段。实际反馈者的同一时段异常堆栈尚未取得，不将离线复现等同于确认其全部故障。

基于官方 `0.7.55.22`、提交 `e578aaec1b58945a8bf6868a5c8ec2baae8bc50b`，套用 .2103 扩展壳，完整保留此次官方新增的深层攻略道具策略。修复 AEAssistV3 内部名称识别，并允许内置移速在最终奖励层生效。现有 GCD 保护、跨模式暂停、保档解锁、地图和其他扩展保留，新增业务继续集中在 `LocalExtensions/`；与旧 `.83` 源码隔离。

AE 的加载名称兼容 `AEAssistV3` 与 `AEAssist`，通信仍使用 `AEAssist.CombatRoutine.*` 原端点；不改用户不可读回的 `/aestop` 锁，AE 仍不参与 GCD 补打。移速仅在 FSD 运行且普通层／最终奖励层有效可步行时启用；Boss、过场、停止和副本外撤销。根据用户随后“直接推送”的指令，通过构建和离线检查后发布原库链，本地保留同字节发布副本；未操作游戏或切换 AE 开关，实战仍待验证。

功能和维护方法见 [使用与换版说明](docs/使用与换版说明.md)、[扩展壳更新规则](docs/本地扩展壳与更新规则.md)与[验收记录](docs/验收记录.md)。OmenTools 完整源码已随附，直接构建无需初始化子模块。

准备 .NET SDK10.0.401 与 Dalamud API15 开发程序集，在本目录执行：

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.ContractTests -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.SpeedChecks -c Release
```

本目录 `NewFolder3-release.json` 保留官方基底原始文件，仅供源码溯源；本修改版的有效库链为仓库根目录 `pluginmaster.json`。扩展更新应在独立官方 Git 基底工作树操作，不要混入旧归档实现。

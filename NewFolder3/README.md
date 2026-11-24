# NewFolder3 0.7.55.2204 构建源码

GCD 停滞保护提供开关和等待秒数（1–300 秒，默认 6 秒）。开启后，只要选中同一可攻击敌人，超过设定时间没有 GCD，就在角色、技能、射程与视线实际可用时尝试一次基础单体 GCD。不限副本、楼层、FSD 运行状态或输出插件，AE、自定义指令同样适用。暂停探索、手动停手及机制故障不自动关闭此保护；完全停手请关闭开关。

同次停滞只尝试一次，底层拒绝也不连续重试，观察到 GCD 冷却后重新计时。能力技、动画锁及短暂超距不抹掉等待时间；读条、冷却及排队期间不发补打。换目标、换职业、修改等待时间或关闭开关会重置。不切目标、不移动。修正动态武器射程 -1 被错误拒绝的校验；不使用 BossMod.ActionDefinitions 单例，不修改原作者战斗、探索、道具、寻路、机制和奖励实现。

官方基底 `0.7.55.22` / `e578aaec1b58945a8bf6868a5c8ec2baae8bc50b`；OmenTools 固定 `6b8f1244bee6a8eca741b8c27e1cfc229ceef66c`。保留 .2203 的 AE 无版本控制诊断、续接宝箱设置、移速、暂停与其他扩展，以及两套签名地图。新增业务继续留在 `LocalExtensions/`，与旧 .83 独立。

功能与维护方法见 [使用与换版说明](docs/使用与换版说明.md)、[扩展壳更新规则](docs/本地扩展壳与更新规则.md)和[验收记录](docs/验收记录.md)。本版直接发布原库链，同时交付约定本地目录；未操作游戏；实际补打与 Boss 主动接敌待游戏验证，Boss 初始化根因仍未修复。

准备 .NET SDK 10.0.401 与 Dalamud API 15 开发程序集，初始化固定 OmenTools 子模块后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Local.ps1
python scripts/test_overlay.py
python scripts/package_local.py --output "C:/Users/24496/Documents/ChatGPT/本地插件dll/NewFolder3/测试版本"
```

`NewFolder3-release.json` 保留官方基底原始文件供溯源；有效库链继续使用仓库根目录 `pluginmaster.json`，本次按用户明确指令，通过必要检查后发布原库链。防踢改为原生输入计时器超时后向本游戏窗口短按 Ctrl 并回读；不再发送静止位置包，实际服务端效果待测。三种自建档模式明确败北后校验删档重开，失败不占成功次数，已增加循环回归；手动暂停／停止、已有档和异常保护保留。最终领奖成功后自动循环需开启已有领奖后收尾开关。

未来更新从本版导出壳，在独立官方基底目录重套并完成回归，不能只复制 DLL。

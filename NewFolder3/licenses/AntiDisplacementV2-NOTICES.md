# 防击退模块来源与集成说明

2026-11-26，从本项目独立实验 AntiDisplacementV2 0.2.0.0 接入，许可证 GPL-3.0-only，见发行根目录 LICENSE。
原实验路径：C:/Users/24496/Documents/ChatGPT/FF14/NewFolder3-Experiments-20261126/AntiDisplacementV2。
Policy.cs 与 NativeContract.cs 保留原样；NativeMotion.cs 仅将卸载改为 finally 确保释放宿主 Hook。
本插件另外实现妖宫 71–99 层的调用限制、配置、说明页开关和生命周期接入；不加载或依赖独立实验插件。

该模块使用 .NET、Dalamud 宿主 Hook API、宿主 ImGui 和 FFXIVClientStructs；不分发这些宿主程序集。
模块不调用 XSZ、ECommons、NekoVerifierLib 的能力、IPC 或配置，不依靠其他插件启用防位移。
主插件自身已有依赖保持不变。

用户提供的 IC 移动与设置附件仅作概念与调用形态参考，未将未声明许可证的附件源码或二进制纳入。
原生函数身份与 ABI 来自本机客户端只读审计；仅覆盖经过已核验原生入口的本人位移请求。
独立实验 0.2.0.0 已获用户反馈本次测试有效，不能扩大为本集成版或全部机制均已实测。

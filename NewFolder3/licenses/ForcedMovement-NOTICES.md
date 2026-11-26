# 内置强制移动来源

源自用户验证的 ForcedMovementLab 0.2.0.1（2026-11-26），AGPL-3.0-or-later。
完整许可见 ForcedMovementLab-AGPL-3.0.txt；原主工程的许可证和其他来源声明保留。
本发行整合作品按 AGPL-3.0-or-later 提供，原有各文件的版权和许可声明保持；完整对应源码随源码包交付。

八权限编号／思路：xa-io / XA-Slave，固定提交 ff4e0778cf81d604a65c8a461b2cef16a324b9a6，
XASlave/Services/PlayerModsService.cs，AGPL-3.0。
https://github.com/xa-io/XA-Slave/blob/ff4e0778cf81d604a65c8a461b2cef16a324b9a6/XASlave/Services/PlayerModsService.cs

Conditions.HasPermission ABI：FFXIVClientStructs，aers，MIT，许可见同目录 ForcedMovement-FFXIVClientStructs-MIT.txt。
研究提交 756bb15df1613be94cf7e542913035e97ed1f232，实际运行采用宿主提供的 SDK。
https://github.com/aers/FFXIVClientStructs/blob/756bb15df1613be94cf7e542913035e97ed1f232/FFXIVClientStructs/FFXIV/Client/Game/Conditions.cs

本整合未带入旧版方向／步行 Hook、独立实验 UI／IPC、试走工具、诊断原始数据或其他插件 DLL。
主插件仅增加地宫范围、现场复查、独立配置和生命周期适配；保留原函数一次调用及两个排除参数，
不写坐标／速度／方向、不发网络包、不删除状态，也不替原导航重新下达任务。
独立用户验证不等于主插件集成版全部限制状态均已验证。
宿主 Hook 登记机制仅作只读研究核对，没有复制 Dalamud 源码或调用其他插件配置。

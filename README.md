# 幻想

FFXIV 国服 Dalamud 插件总库。所有自制插件的源码、安装清单和发布包统一维护在此仓库。

## 固定插件库地址

在 Dalamud 设置的 Experimental → Custom Plugin Repositories 中添加：

`https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main/pluginmaster.json`

该地址固定指向本仓库根目录清单。后续插件以独立条目追加到 `pluginmaster.json`，并在仓库中保留各自源码及发布包。

## 当前插件

- [直暴播报](幻想/直暴播报/README.md)：在战斗中把自己造成的直击、暴击和直暴技能及伤害输出到 Dalamud 聊天。

## 参考实现

- [Dalamud SamplePlugin](https://github.com/goatcorp/SamplePlugin)：SDK、服务注入和构建约定。
- [Hit it, Joe!](https://github.com/Berna-L/ffxiv-tf2-crit-plugin)：FlyText 事件及暴击/直击类型的处理思路。

## 构建

需要 .NET 10 SDK 和 Dalamud 开发环境：

```powershell
dotnet build .\\幻想\\直暴播报\\CritChat.csproj -c Release
```

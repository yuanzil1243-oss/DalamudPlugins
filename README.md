# 幻想

FFXIV 国服 Dalamud 插件集合。本仓库当前项目：

- [直暴播报](幻想/直暴播报/README.md)：将自己造成的直击、暴击与直暴技能和伤害输出到游戏聊天。

## Dalamud 自定义插件库

仓库根目录的 `pluginmaster.json` 是卫月自定义插件库清单。发布后，在 Dalamud 设置的 Experimental → Custom Plugin Repositories 中添加：

`https://raw.githubusercontent.com/yuanzil1243-oss/Huanxiang/main/pluginmaster.json`

该清单格式与 Dalamud 第三方插件库一致，采用的短链长度和结构参照用户提供的截图。

## 参考实现

- [Dalamud SamplePlugin](https://github.com/goatcorp/SamplePlugin)：SDK、服务注入和构建约定。
- [Hit it, Joe!](https://github.com/Berna-L/ffxiv-tf2-crit-plugin)：使用 FlyText 事件处理暴击/直击类型；本项目复用事件类型并将结果写入聊天。

## 构建

需要 .NET 10 SDK 和 Dalamud 开发环境。运行：

```powershell
dotnet build .\幻想\直暴播报\CritChat.csproj -c Release
```


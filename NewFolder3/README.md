# 构建 NewFolder3 0.7.55.83-final-local

这是基于 `.82-final-local` 第2次打包版的本地修改版源码，本次仅新增原作者更新确认提示。

原作者已经完善了死宫、天宫、妖宫的下底功能，推荐使用原作者版本。

- [原作者仓库](https://github.com/transparent-duck/NewFolder3)
- 原作者库链：

```text
https://raw.githubusercontent.com/transparent-duck/NewFolder3/refs/heads/main/NewFolder3-release.json
```

安装 `global.json` 指定的.NET SDK，准备国服 Dalamud API15 开发程序集。OmenTools公开源码已随附，无须初始化子模块。

```powershell
dotnet build NewFolder3.csproj -c Release -p:Platform=x64
dotnet run --project tests/NewFolder3.ContractTests -c Release
dotnet run --project tests/NewFolder3.MechanicTests -c Release
dotnet run --project tests/NewFolder3.RestExitTests -c Release
```

969+250+22=1241项离线检查通过，构建0错误、14项既有警告。新增提示窗尚待游戏界面验证。保留公开地图签名校验，不附带个人日志、配置、SDK、私有服务或上传端点。详见上级 `docs/发布说明-0.7.55.83-final-local.md`。

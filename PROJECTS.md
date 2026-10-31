# 项目结构与开发 / Projects and Development

本仓库仅公开 PC 和 Android 两个客户端，各自独立构建。PC 和 Android 的协议及行为变更应保持一致，同时保留各平台的界面和输入适配。

| 项目 | 路径 | 当前版本 |
| --- | --- | --- |
| PC SMAPI 客户端 | `vivantvalley_no_langgraph/` | `0.23.4` |
| Android SMAPI 客户端 | `vivantvalley_android/` | `0.14.3-alpha.44` |

## 客户端构建

需要 .NET SDK 和合法安装的 Stardew Valley / SMAPI 引用。游戏程序集不随源码分发。主项目目标为 `net6.0`；`ConversationEngineSmoke` 项目目标为 `net10.0`，运行该测试需要 .NET 10 SDK。开发时显式传入自己的游戏安装路径，覆盖项目中历史开发机的默认路径。

在仓库根目录执行（将示例路径替换为自己的安装路径）：

```sh
dotnet build vivantvalley_no_langgraph/VivantValley.csproj -c Release -p:GamePath="/path/to/Stardew Valley"
dotnet build vivantvalley_android/VivantValley.csproj -c Release -p:GamePath="/path/to/Stardew Valley"

dotnet run --project vivantvalley_no_langgraph/tests/ConversationEngineSmoke/ConversationEngineSmoke.csproj -c Release -p:GamePath="/path/to/Stardew Valley"
dotnet run --project vivantvalley_android/tests/ConversationEngineSmoke/ConversationEngineSmoke.csproj -c Release -p:GamePath="/path/to/Stardew Valley"
```

两个客户端源码互不引用；构建输出位于各自 `bin/Release/net6.0/`。发布时需要同版 DLL、`manifest.json`、完整 `assets/` 与 `i18n/`；不要分发游戏程序集、个人配置或聊天数据。安装应使用 GitHub Release 中的平台 ZIP。

## 当前发布的验证范围

2026-10-31 发布的 PC `0.23.4` 与 Android `0.14.3-alpha.44` 已完成两端 Release 构建和 `ConversationEngineSmoke` 测试，安装包内容和 SHA-256 已核对。构建及自动化测试不替代真实游戏与 Android 设备验收。

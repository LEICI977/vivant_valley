# Vivant Valley

让星露谷里的每一次相遇，成为会继续生长的故事。NPC 会带着自己的性格、关系和生活轨迹面对你，也会记住共同经历，在下次见面时提起。

Vivant Valley brings personality-driven AI dialogue, shared memories, companion activities, and ongoing stories to Stardew Valley.

## 下载 / Downloads

更新日期 / Updated: **2026-10-31**

| 平台 / Platform | 版本 / Version | 安装包 / Package |
| --- | --- | --- |
| PC（Windows / macOS / Linux） | **0.23.4** | [VivantValley-Desktop-0.23.4.zip](https://github.com/LEICI977/vivant_valley/releases/download/v0.23.4/VivantValley-Desktop-0.23.4.zip) |
| Android（适配验证版 / Preview） | **0.14.3-alpha.44** | [VivantValley-Android-0.14.3-alpha.44.zip](https://github.com/LEICI977/vivant_valley/releases/download/v0.23.4/VivantValley-Android-0.14.3-alpha.44.zip) |

[完整更新说明、安装说明和 SHA-256 校验 / Release notes, installation and checksums](https://github.com/LEICI977/vivant_valley/releases/tag/v0.23.4)

PC 和 Android 分别维护、分别构建，请下载与平台对应的安装包。Android 版本仍需在目标设备上验收；通过源码构建与自动化测试不代表已完成所有设备兼容验证。

当前客户端使用模组内的 C# 引擎校验、执行游戏行动，并直接连接托管服务或玩家配置的 AI API。无需安装 Python 或 LangGraph，也不会为对话启动本地后端进程。本仓库仅公开 PC 和 Android 客户端源码。

Both clients use an in-process C# conversation engine. No Python or LangGraph installation is required. This repository publishes only the desktop and Android client sources.

## 本次更新 / This Update

- 修复模型在移动尚未执行时提前返回 `travel_barks` 导致的对话失败。决策阶段明确要求空数组；协议不合规时仅纠正重试一次，仍失败则显示本地化提示。
- 保留移动成功、失败、取消及行动已经提交后的正确结果语义，避免误报“尚未执行”。
- 同步两端托管账户、余额和语音目录刷新及语音试听兼容处理。
- 提供两端完整运行资源、双语提示词、安装说明和文件校验值。

This release fixes premature travel dialogue in model decisions, adds a bounded correction retry and localized error messages, and includes hosted-account and speech-catalog synchronization fixes.

## 核心功能 / Features

- **有性格的对话与记忆**：结合 NPC 人格、关系、日期、天气和实时场景，保留共同经历与长期摘要。
- **真实游戏行动**：礼物、同行、钓鱼和战斗由 C# 引擎检查并在游戏线程执行；执行结果回到对话中。
- **持续发展的故事**：主动相遇、邀请信、每周剧情和梦境对话。
- **双语体验与语音**：中文和英文提示词、界面，以及托管语音设置。
- **托管账户或直连 API**：支持托管账户登录、模型目录和兑换码，也保留高级直连设置。
- **独立手机适配**：Android 触摸操作、原生输入和响应式菜单单独维护。

## 安装 / Installation

1. 安装适合所在平台及游戏版本的 SMAPI；具体平台说明见下方客户端文档。
2. 下载对应的 **Desktop** 或 **Android** ZIP，并关闭游戏。
3. 升级前备份旧 Mod 目录中的 `config.json`、自定义提示词及需要保留的数据。
4. 解压 ZIP 内的 `VivantValley` 文件夹到游戏 `Mods` 目录，确认路径为 `Mods/VivantValley/manifest.json`。
5. 按发布包附带的安装说明恢复需要保留的配置；通过 SMAPI 启动游戏并完成账户或 API 设置。

同一平台只保留一个 Vivant Valley 安装目录，避免同时加载旧版和新版。当前 SMAPI UniqueID 保持为 `firstmod.StardewAIMemories`。

Extract the package's `VivantValley` folder into `Mods`. Back up your configuration and custom content before upgrading. See the installation file attached to the release for detailed instructions.

## 源码与文档 / Source and Documentation

| 目录 / Directory | 用途 / Purpose | 文档 / Documentation |
| --- | --- | --- |
| `vivantvalley_no_langgraph/` | 独立 PC SMAPI 客户端 / Desktop client | [README](vivantvalley_no_langgraph/README.md) |
| `vivantvalley_android/` | 独立 Android SMAPI 客户端 / Android client | [README](vivantvalley_android/README.md) · [Android 操作与验收](vivantvalley_android/ANDROID.md) |

构建和测试方法见 [PROJECTS.md](PROJECTS.md)。客户端的 `manifest.json` 和 `VivantValley.csproj` 是版本依据；较早的专题文档可能描述历史版本。

历史 0.13.0 介绍保留在 [docs/README-0.13.0.md](docs/README-0.13.0.md)。根目录原有的 `VivantValley2.0.zip` 是历史归档；当前安装包请使用上方 Release。

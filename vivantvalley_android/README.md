# Vivant Valley

**本目录是安卓版独立源码：`vivantvalley_android/`。** 后续手机交互、输入法和界面适配在此维护。电脑版基线位于相邻的 `vivantvalley_no_langgraph/`，两边分别构建和打包，源码互不引用。目录管理说明见工作区根目录的 [PROJECTS.md](../PROJECTS.md)。

安卓适配验证版：`0.14.3-alpha.8`。修复 ZIP 路径兼容、菜单重复读取触摸状态和模拟鼠标被抑制的问题，并适配 SMAPI Launcher 的原生输入窗口。手机版 UI 已在代码中固定启用，无需创建或编辑 config.json。右下角独立按钮取代长按 NPC，支持附近 NPC 选择、按钮大小与位置设置、独立首次配置引导；保留手机当前输入法、触摸滚动与响应式菜单。目标安卓环境仍需真机验收。操作与验收步骤见 [ANDROID.md](ANDROID.md)。

中英文现已跟随星露谷语言设置：提示词分别在`assets/prompts/zh/`与`assets/prompts/en/`编辑；普通界面、邀请信标签和预置剧情提供双语，其他游戏语言暂回退英文。更新时安装DLL、i18n及两套提示词。旧生成内容保留原文，详见`assets/prompts/README.md`。

English now follows Stardew Valley's language setting. Edit prompts in `assets/prompts/en/` or `zh/`. Install the DLL, i18n folder and both prompt folders together. Previously generated content keeps its original text.

普通对话提示词与上下文开关统一在 [`assets/prompts/`](assets/prompts/README.md) 管理。默认不发送技能、进度、钥匙、姜岛船和任务清单；安装新版后，编辑 Mod 内的提示词文件，下一轮对话生效。

周剧情建议两次选择、10～18节点，支持更多选择与分页选项；心数和配角身份仅作创作背景，不再硬性否决剧情。完整剧情容许最多48节点，不要求凑齐三类结局或必须条件台词。题材与格式分别在`assets/prompts/zh/`或`en/`中的`weekly-story.txt`和`weekly-story-format.txt`编辑，旧单选择和三选择存档继续兼容，详见上述配置说明。

## 中文

Vivant Valley 不只是给每个村民套上聊天窗口，而是让每一次相遇都成为会继续生长的故事。NPC 会带着自己的性格、关系和生活轨迹面对你：他们可能答应邀请，也可能因为当下的心情和立场拒绝；他们会记住共同经历，并在下一次见面时用自己的方式提起它。

对话会结合日期、天气、地点、任务、玩家近期活动、原版剧情和实时场景快照。AI 只提出行动意图，真正的礼物、移动、钓鱼和战斗都由模组内的 C# 引擎校验并在游戏线程执行，结果会再回到对话中。

发布包只包含 SMAPI 模组 DLL 和数据文件；不包含 Python、独立后端或可执行子进程，也不会监听本机端口。DLL 直接连接玩家配置的 AI Base URL；远程地址必须使用 HTTPS，仅用户主动配置的回环地址可使用 HTTP。

当前源码版本：`0.14.3-alpha.1`（安卓适配验证版）

## English

Vivant Valley is more than a chat window attached to every villager. It turns each encounter into a story that can continue. NPCs meet you with their own personalities, relationships, and daily lives: they may accept an invitation or refuse it based on their mood and motives, remember what you experienced together, and bring it up in their own way later.

Dialogue is grounded in the date, weather, location, quests, recent player activity, vanilla story facts, and a live scene snapshot. The AI only proposes an action. Gifts, travel, fishing, and combat are validated and executed by the in-process C# engine on the game thread, then the authoritative result returns to the conversation.

The release contains only the SMAPI mod DLL and data files. It includes no Python runtime, standalone backend, or child executable, and opens no local listening port. The DLL connects directly to the AI Base URL configured by the player. Remote URLs must use HTTPS; HTTP is allowed only for a loopback URL explicitly configured by the user.

Current source version: `0.14.3-alpha.1` (Android adaptation preview; device validation pending).

## 核心功能 / Core Features

### 中文

- **角色化 AI 对话**：靠近并面向村民，按 `Space` 开始对话。模型会参考星露谷原版角色的性格、语气、价值观和经历。
- **现场上下文**：日期、天气、地点、任务、玩家进度、近期活动、原版剧情事实和 NPC 身边正在发生的事都会进入本轮情境。
- **共同记忆**：保留近期聊天、临时同行经历和压缩后的长期摘要；长期记忆只在与本轮话题相关或玩家主动回忆时召回，重复事实会合并，记忆不会覆盖实时事实或角色性格。
- **真实工具执行**：AI 只提出行动意图；进程内 C# 引擎校验白名单、请求玩家确认、执行并回传权威结果。礼物、移动、钓鱼和战斗不会停留在台词里。
- **同行旅行**：NPC 接受邀请后由玩家带路，跟随玩家前往目标地点并处理地图、房屋和矿洞切换；活动结束后恢复日程，没有日程时会回家。
- **矿洞护卫**：NPC 使用默认银河剑，主动寻找附近怪物并作战。NPC 有生命条；被击败会损失半颗心、住院一天，并记住这次经历。
- **钓鱼伙伴**：玩家到达钓鱼点并抛竿后，NPC 使用铱金鱼竿完成抛竿、等待、收杆和捕获，真实鱼获会交给玩家。
- **有分寸的社交与礼物**：NPC 会根据性格、关系和情境决定是否接受请求。礼物来自代码控制的合法候选池，支持当面交付和次日惊喜邮件。
- **主动相遇**：社交导演依据原版日程、近期互动和关系安排自然搭话；合适时 NPC 也可能带着礼物或邮件来找你。
- **每周剧情**：在真实关系、日期、天气、可用角色和地点约束下生成可演出的恋爱事件，包含铺垫、推进、反转、玩家选择与收束，支持最多 32 个安全节点。
- **托管账户**：首次进入存档时可直接在游戏内注册/登录 Vivant Valley 账户；模型请求自动路由到官方服务，玩家无需填写上游 API Key 或创建设备 Key。登录后可选择服务端公开的模型别名，并使用兑换码充值。
- **可配置体验**：保留 DeepSeek、OpenAI、兼容 Base URL 和自定义模型的高级直连模式；可分别调整对话窗口与主动对话窗口大小。

### English

- **Personality-driven dialogue**: Face a nearby villager and press `Space`. Responses follow the character's original Stardew Valley personality, voice, values, and history.
- **Live context**: Date, weather, location, quests, player progress, recent activity, vanilla story facts, and nearby events can inform each turn.
- **Shared memory**: Recent dialogue, temporary travel facts, and bounded long-term summaries preserve continuity. Long-term facts are recalled only when relevant or explicitly requested, duplicates are merged, and memory never overrides live facts or personality.
- **Real tool execution**: The AI only proposes an intent. The in-process C# engine validates allowlists, asks for player confirmation, executes on the game thread, and reports the authoritative result. Gifts, travel, fishing, and combat are real game actions.
- **Shared travel**: After accepting an invitation, an NPC follows the player through map, house, and mine transitions, then resumes their schedule. NPCs without a usable schedule go home.
- **Mine guard**: The NPC uses a default Galaxy Sword and actively fights nearby monsters. NPCs have health; defeat costs half a heart, sends them to the hospital for one day, and becomes a remembered event.
- **Fishing companion**: After the player reaches a fishing spot and casts, the NPC uses an Iridium Rod for casting, waiting, reeling, and catching. Real fish are delivered to the player.
- **Consent-based gifts and actions**: NPCs decide whether to accept requests based on personality, relationship, and context. Gifts come from a code-controlled valid pool and may arrive face-to-face or by surprise mail.
- **Proactive encounters**: The social director uses vanilla schedules, recent interactions, and relationships to arrange natural conversations, gifts, and letters.
- **Weekly stories**: Executable romance events are generated under live relationship, date, weather, cast, and location constraints, with setup, escalation, a reversal, a player choice, and a resolution across as many as 32 safe beats.
- **Configurable experience**: Supports DeepSeek, OpenAI, compatible Base URLs, custom models, and separate window scaling.

## 运行要求 / Requirements

### 中文

- Stardew Valley 1.6 或更高版本
- SMAPI 4.0 或更高版本
- Vivant Valley 托管账户（推荐），或高级直连模式下的 DeepSeek/OpenAI API Key
- 可以访问 Vivant Valley 服务或所选 AI 提供商的网络环境

Vivant Valley 不捆绑 AI 模型。API 调用可能产生由对应提供商收取的费用。

### English

- Stardew Valley 1.6 or later
- SMAPI 4.0 or later
- A DeepSeek or OpenAI API key
- Internet access to the selected AI provider

Vivant Valley does not bundle an AI model. API usage may incur fees charged by the selected provider.

## 安装 / Installation

### 中文

1. 安装 Stardew Valley 1.6 和 SMAPI 4.x。
2. 下载 `VivantValley-Release.zip`；同一个包适用于 SMAPI 支持的 Windows、macOS 和 Linux。
3. 将压缩包内的 `VivantValley` 文件夹解压到游戏的 `Mods` 目录。
4. 确认最终结构为 `Mods/VivantValley/manifest.json`，不要多套一层文件夹。
5. 通过 SMAPI 启动游戏，载入存档后完成 AI 提供商设置。

### English

1. Install Stardew Valley 1.6 and SMAPI 4.x.
2. Download `VivantValley-Release.zip`; the same package works on SMAPI-supported Windows, macOS, and Linux systems.
3. Extract the included `VivantValley` folder into the game's `Mods` directory.
4. Verify that the final path is `Mods/VivantValley/manifest.json`; do not add an extra directory layer.
5. Launch through SMAPI, load a save, and complete the AI provider setup.

### 从 Stardew AI Memories 升级 / Upgrading

`Vivant Valley 0.13.0` 是原项目的正式改名版本。SMAPI `UniqueID` 继续使用 `firstmod.StardewAIMemories`，已有聊天记忆、社交计划和邮件状态保持兼容。

`Vivant Valley 0.13.0` is the official renamed version of the original project. The SMAPI `UniqueID` remains `firstmod.StardewAIMemories`, so existing conversation memories, social plans, and mail state remain compatible.

升级时请关闭游戏，备份旧 `config.json`，移除旧的 `Mods/StardewAIMemories` 文件夹，安装新的 `Mods/VivantValley`，再将旧配置复制到新目录。不要同时保留新旧两个安装目录。

When upgrading, close the game, back up the old `config.json`, remove the old `Mods/StardewAIMemories` folder, install `Mods/VivantValley`, and copy the old configuration into the new folder. Do not keep both installation folders.

## 初次配置 / First-Time Setup

### 中文

载入存档后，默认会打开 Vivant Valley 托管账户窗口：注册时先输入邮箱发送 6 位邮箱验证码，再填写至少 12 位密码完成注册；已有账户可直接登录。忘记密码时可在同一窗口发送重置验证码并设置新密码。登录成功后模型别名会从服务端目录自动读取（默认 `vv-dialogue`），并可在窗口中切换为其他公开别名；兑换码也可在同一窗口直接充值。

如需使用自己的上游 Key，在托管账户窗口点击“直连 API 设置”，或运行 `vivant_settings`，再切换到 DeepSeek 或 OpenAI。两种直连配置会分别保存；以后可随时切回 Vivant Valley 托管账户，不会丢失原来的 Key、Base URL 或模型设置。

- DeepSeek：默认 Base URL 为 `https://api.deepseek.com`。
- OpenAI：默认 Base URL 为 `https://api.openai.com/v1`。
- Base URL 填写服务根地址；客户端会自动规范化并追加聊天完成端点。
- 模型名称必须是当前 API Key 有权访问的模型。

也可以使用环境变量 `DEEPSEEK_API_KEY` 或 `OPENAI_API_KEY`。游戏内保存的提供商 Key 优先于环境变量。

### English

After loading a save, the Vivant Valley hosted-account window opens by default.
Registration sends a six-digit email code before accepting the 12-character
password; existing accounts can log in directly. The same window can request a
password-reset code and set a new password. After login, model aliases are read
from the server catalog (default `vv-dialogue`), and redeem codes can be applied
there. To use your own provider instead, choose the direct API settings:

- DeepSeek: default Base URL is `https://api.deepseek.com`.
- OpenAI: default Base URL is `https://api.openai.com/v1`.
- Enter the service root as the Base URL; the client normalizes it and appends the chat-completion endpoint.
- Use a model name that the configured API key can access.

Keys can also be supplied through `DEEPSEEK_API_KEY` or `OPENAI_API_KEY`. A provider key saved in-game takes precedence.

## 使用方法 / Usage

| 操作 / Action | 默认方式 / Default |
| --- | --- |
| 开始对话 / Start | 靠近并面向 NPC，按 `Space` / Face a nearby NPC and press `Space` |
| 继续 / Continue | 回复结束后按 `Enter` 或点击“继续” / Press `Enter` or click Continue |
| 关闭 / Close | 按 `Esc` 或点击“关闭” / Press `Esc` or click Close |
| AI 设置 / AI settings | 设置按钮或运行 `vivant_settings` / Settings button or `vivant_settings` |
| 查看状态 / Status | `vivant_status` |
| 查看 AI 请求 / AI request capture | `vivant_ai_debug on|off|status` |
| 清除记忆 / Forget | `vivant_forget <NPC内部名\|all>` |
| 社交计划 / Social plan | `vivant_social_status [NPC内部名]` |
| 已报告用量 / Reported usage | `vivant_ai_usage` |
| 每周剧情状态 / Weekly story status | `vivant_story_status` |

旧命令 `aimemory_key`、`aimemory_settings`、`aimemory_status`、`aimemory_forget` 和 `aisocial_status` 仍可使用。 / Legacy commands remain available for compatibility.

## 距离与地图规则 / Distance and Map Rules

### 中文

- 开始普通对话时，玩家和 NPC 必须在同一地图、默认 `3.5` 格内。
- 对话框打开后，走远、NPC 移动或换地图不会中断当前对话。
- 主动相遇只在 NPC 按原版日程与玩家同地图且进入默认 `7` 格范围时触发。
- 同行需要 NPC 真实接受；玩家带路，NPC 在约一格距离内跟随，并延迟进入地图、房屋或矿洞，避免挡路。
- 同行活动结束后 NPC 恢复原版日程，没有日程时返回家中。

### English

- To start a normal conversation, the player and NPC must be on the same map and within the default `3.5`-tile range.
- Once open, walking away, NPC movement, or changing maps does not interrupt the active conversation.
- Proactive encounters trigger only when the NPC is on the same map under their vanilla schedule and enters the default `7`-tile range.
- Shared activities require actual NPC acceptance. The player leads, the NPC follows at roughly one tile, and transitions are delayed so the player is not blocked.
- After the activity ends, the NPC resumes their vanilla schedule or goes home if no usable schedule exists.

## 对话、记忆与剧情事实 / Dialogue, Memory, and Story Facts

### 中文

每次对话会组合当前游戏事实、NPC 原版人格提示、近期聊天、按当前话题筛选的长期摘要、原版剧情记录和实时场景快照。当前存档事实优先于旧记忆；历史记录不是必须继续的待办，玩家换题、拒绝、回答或完成事项后，NPC 不应重新追问。长期记忆会合并重复事实，并优先保留明确承诺、选择、关系变化与真实游戏结果。最近 8 轮的真实共同经历只保留在临时会话中，跨天或重新载入存档后清除。AI 对话不替代原版每日交谈，也不会直接增加好感度。

### English

Each conversation combines live game facts, the NPC's vanilla personality, recent chat, topic-relevant long-term memory, vanilla story records, and a live scene snapshot. Current save facts outrank memory. History is not an agenda: once the player changes subject, refuses, answers, or completes something, the NPC should not reopen it. Duplicate memories are merged, while explicit promises, choices, relationship changes, and authoritative game outcomes receive retention priority. Temporary shared experiences remain for eight conversation turns and are cleared across days or reloads. AI dialogue neither replaces vanilla daily dialogue nor directly grants friendship.

## 2026-09-08 记忆与周剧情改进

约定在本地保存为待履行、已完成、已取消或已过期，并单独保存停止追问标记。无关闲聊不会抹掉未来约定；“不要再提矿洞的约定”会停止主动追问，只问“还记得吗”不会自动重开已取消的约定。明确的新约定可以重新激活。当前规则识别明确表述和常见活动主题，不能完整理解任意自然语言。明天、后天、下周及明确“第 N 天”用于到期判断；有日期的约定在到期后保留一天宽限，无日期的约定十四天后停止主动跟进。

每周剧情最多 32 个演出节点，包含一个主选择和 2–3 个分支回应。校验先修正可确认的字段偏差，再检查选择是否真正可达；不会靠删除重要台词使剧情通过。只有玩家作出选择且分支回应实际显示后才记录完成，跳过或中断不算完成。规划摘要用于策划，完成记忆以实际选择和回应为准。

每个存档、玩家、周最多两次自动生成请求，已发送后取消也计入。调用记录位于模组目录 `data/weekly-story-budget/`，跨天或重新读档不会恢复次数；成功结果可直接复用。无可用候选时等待条件变化，不消耗请求。两次均失败会保留原因并停止本周自动请求，可用 `vivant_story_status` 查看。这项限制用于控制成本，不能承诺任意模型响应都一定生成成功。

世界事实、剧情事实和召回记忆分别保持 300、250、350 字符上限；按类别优先保留配偶、住院限制、解锁和实际选择。`WeeklyStoryMaxOutputTokens` 默认4000，首次更新会将旧2400一次性迁移到4000；实际计费取决于模型及实际用量。`vivant_ai_usage` 显示本次启动收到的非流式完整响应中，服务端已报告的输入/输出 token；缺失、流式及未收到响应的请求不计入。该统计不保留对话正文或密钥，也不发出额外请求。

## 主动社交 / Proactive Social

### 中文

社交导演根据近期积极对话、温暖度、关心度、当前仍未完成的明确约定、关系和相遇多样性选择候选。只有最新一次对话信号可以声明约定仍未完成，较早记录不会永久驱动 NPC 反复追问。键鼠模式每天 `3-5` 名；手柄模式最多 `6` 名；每名候选有早上和下午两个候补相遇窗口，但当天成功相遇一次后另一个窗口会关闭，避免同一 NPC 重复搭话。玩家活动只按天保存地点类别、物品类别、技能变化和活跃时段等摘要，默认保留 7 天。

### English

The social director selects candidates using recent positive dialogue, warmth, care, currently pending explicit commitments, relationships, and encounter variety. Only the newest conversation signal can keep a commitment open, so stale records cannot make an NPC repeat the same follow-up indefinitely. Keyboard and mouse mode uses `3-5` candidates per day; controller mode can use up to `6`. Each NPC gets morning and afternoon fallback windows, but the second closes after one successful encounter that day. Player activity is retained only as a seven-day daily summary of location categories, item categories, skill changes, and active periods.

## 礼物规则 / Gift Rules

### 中文

礼物目录位于 `assets/social/gift-pools.json`。AI 只能看到代码预先筛选的候选 key，不能提交任意物品 ID。代码会拒绝无效候选、未达到红心要求的候选、工具/武器/任务物品、重复行动和冷却中的礼物。`give_gift` 会先完成真实交付，再生成可见回复；背包已满时物品会落在玩家脚边。隔夜邮件每天最多安排 `0-2` 封，并使用原版可点击邮件机制。

### English

The gift catalog is `assets/social/gift-pools.json`. The AI sees only code-filtered candidate keys and cannot submit arbitrary item IDs. Code rejects invalid candidates, insufficient-heart gifts, tools, weapons, quest items, duplicate actions, and gifts on cooldown. `give_gift` completes real delivery before the visible reply; a full inventory drops the item near the player. Overnight planning may select `0-2` surprise mails using the vanilla clickable mail system.

## 主要配置 / Main Configuration

SMAPI 首次运行后会在模组目录生成 `config.json`。/ SMAPI creates `config.json` in the Mod folder on first run.

| 配置项 / Setting | 默认值 / Default | 说明 / Description |
| --- | ---: | --- |
| `ChatKey` | `Space` | 普通对话快捷键 / Normal conversation key |
| `MaxTalkDistanceTiles` | `3.5` | 普通对话距离 / Conversation range |
| `WriteAiRequestDebugFiles` | `false` | 将完整请求体写入 `debug-ai-requests` / Write complete request bodies to `debug-ai-requests` |
| `EnableThinking` | `false` | 兼容旧配置，运行时始终关闭思考 / Retained for compatibility; always disabled at runtime |
| `ReasoningEffort` | `low` | 兼容旧配置，不再发送 / Retained for compatibility; no longer sent |
| `MaxContextMessages` | `24` | 每次请求的近期消息上限 / Recent messages per request |
| `SummaryTriggerMessages` | `24` | 触发长期摘要的消息数 / Summary trigger count |
| `SummaryKeepRecentMessages` | `8` | 摘要后保留的消息数 / Messages kept after summary |
| `EnableSocialDirector` | `true` | 每日主动社交 / Daily proactive social |
| `DailyCandidateMin` / `DailyCandidateMax` | `3` / `5` | 键鼠候选；手柄最多 6 名 / Keyboard candidates; up to 6 with controller |
| `SocialActivationDistanceTiles` | `7` | 主动相遇距离 / Proactive encounter range |
| `EnableConversationSignalAnalysis` | `true` | 对话信号提取 / Conversation signal extraction |
| `EnableOvernightMailGifts` | `true` | 隔夜惊喜邮件 / Overnight surprise mail |
| `MaxOvernightMailGifts` | `2` | 每日邮件上限 / Daily mail limit |
| `EnableWeeklyStories` | `true` | 每周可演出恋爱剧情 / Weekly executable romance story |
| `WeeklyStoryMinimumHearts` | `2` | 非配偶恋爱剧情最低红心 / Minimum hearts for non-spouse romance stories |
| `WeeklyStoryMaxOutputTokens` | `4000` | 周剧情单次规划输出上限；旧2400一次性迁移 / Weekly planning output cap; one-time migration from 2400 |

旧版字段仅为兼容已有配置保留，当前流程不会使用。/ Legacy fields are retained only for configuration compatibility and are not used by the current flow.

## 数据与隐私 / Data and Privacy

发送给 AI 提供商的内容可能包括玩家输入、近期对话、长期摘要、有限活动摘要，以及当前日期、地点、天气、关系、任务和已发生剧情事实。这些内容和 API Key 由模组 DLL 直接发送到玩家填写的 Base URL，不经过 Vivant Valley 自有服务器。使用第三方兼容 Base URL 时，应先确认其隐私政策。API Key 通过游戏内设置保存时会以明文写入模组目录的 `config.json`，不会进入存档、日志或发布包。不要分享 `config.json`。

Content sent to the selected AI provider may include player input, recent dialogue, long-term summaries, limited activity summaries, and current date, location, weather, relationship, quests, and completed story facts. The mod DLL sends this content and the API key directly to the Base URL entered by the player; it does not pass through a Vivant Valley server. Review the privacy policy of any third-party compatible Base URL before using it. An API key saved in-game is written in plain text to `config.json` in the Mod folder and is not stored in saves, logs, or release packages. Do not share `config.json`.

启用 `WriteAiRequestDebugFiles` 后，每次请求体会以格式化 JSON 保存到模组目录的 `debug-ai-requests`。文件包含 messages、tools、模型、提供商和端点，但不会包含 `Authorization` 请求头或 API Key。查看完成后运行 `vivant_ai_debug off`，不再需要时删除旧调试文件。

When `WriteAiRequestDebugFiles` is enabled, each request body is also saved as formatted JSON under the mod folder's `debug-ai-requests` directory. The files include messages, tools, model, provider, and endpoint, but never the `Authorization` header or API key. Use `vivant_ai_debug off` after inspection and delete old capture files when they are no longer needed.

## 联机说明 / Multiplayer

普通 AI 对话支持本地分屏和远程农场助手，请求状态按屏幕隔离。长期持久化、每日社交规划、主动相遇、礼物和隔夜邮件目前只由主玩家执行；农场助手的普通聊天记忆只保留到本次连接结束。

Normal AI dialogue supports local split-screen and remote farmhands with per-screen request state. Long-term persistence, daily social planning, proactive encounters, gifts, and overnight mail currently run only for the main player. Farmhand conversation memory lasts only for the current connection.

## 开发与构建 / Development and Build

项目默认引用路径为 `E:\SteamLibrary\steamapps\common\Stardew Valley`。/ The default game reference path is `E:\SteamLibrary\steamapps\common\Stardew Valley`.

Release 包 / Release package:

```powershell
.\scripts\package.ps1 -Configuration Release
```

如果游戏安装在其他位置 / If the game is installed elsewhere:

```powershell
.\scripts\package.ps1 -Configuration Release -GamePath "D:\Games\Stardew Valley"
```

手动构建 Mod / Build the Mod manually:

```powershell
dotnet build .\VivantValley.csproj -c Release
```

冒烟测试 / Smoke test:

```powershell
dotnet run --project .\tests\ConversationEngineSmoke\ConversationEngineSmoke.csproj -c Release
```

发布输出位于 `dist/VivantValley/` 和 `dist/VivantValley-Release.zip`。/ Release output is placed in `dist/VivantValley/` and `dist/VivantValley-Release.zip`.

## 可选服务器代理 / Optional AI Backend

仓库中的 `backend/` 是一个无第三方依赖的 Node.js OpenAI 兼容代理。它可以把游戏请求转发到 DeepSeek、OpenAI 或其他兼容服务，并将上游 API Key 保留在服务器上。启动方式和环境变量见 [`backend/README.md`](backend/README.md)。

The `backend/` directory contains a dependency-free Node.js OpenAI-compatible proxy. It forwards game requests to DeepSeek, OpenAI, or another compatible service while keeping the upstream API key on the server. See [`backend/README.md`](backend/README.md) for setup and deployment.

本机测试时，在模组设置中将 Base URL 设为 `http://127.0.0.1:8787/v1`，API Key 填服务器的 `BACKEND_API_KEY`。跨机器访问必须通过 HTTPS 反向代理；模组会拒绝非回环地址的明文 HTTP。/ For local testing set the mod Base URL to `http://127.0.0.1:8787/v1` and use the server's `BACKEND_API_KEY` as the API key. Cross-machine access must use an HTTPS reverse proxy; the mod rejects plain HTTP for non-loopback addresses.

## 兼容身份 / Compatibility Identity

虽然产品名称和 DLL 已改为 Vivant Valley，SMAPI `UniqueID` 仍为 `firstmod.StardewAIMemories`。该值是已有存档数据和升级识别的一部分，不应在后续版本中修改。

Although the product name and DLL are now Vivant Valley, the SMAPI `UniqueID` remains `firstmod.StardewAIMemories`. It is part of existing save data and upgrade detection and should not be changed in future versions.


### 原版回复框 / Native reply box

手动聊天回复复用游戏 `DialogueBox` 的正文、头像、名字、打字机和分页显示。
AI 请求结束后才显示完整回复；生成期间提供“取消生成”。对话框上方是一排独立操作按钮，
读完最后一页才出现继续聊天及可用的分享/性格微调功能。点击正文或按 Enter/空格补全当前页、翻页；普通回复读完最后一页后，Enter 返回输入框继续聊天。
正文点击和空格在最后一页保留显示。Enter 不会接受行动或自动发送请求，也不会执行原版关闭回调。
行动确认通过“接受/拒绝”按钮选择；Esc 表示拒绝。请求失败可返回输入界面。
回复作为无副作用的文本行交给原版显示层，不进入游戏对白命令、礼物或剧情解析流程。
屏幕尺寸变化只调整显示比例和按钮位置，不重置当前阅读页。

Manual replies now use the game's `DialogueBox`. Complete responses are shown with native typing and pagination;
an external single-row toolbar handles cancellation, continuation, sharing (where available), and personality settings.
Reading clicks and Enter/Space never accept actions or invoke native close callbacks.
After the final reply page is fully displayed, Enter returns to the composer; clicks and Space keep the page open.
Only explicit Accept/Decline buttons resolve confirmations; Escape declines. Replies enter the view as literal text,
without executing dialogue commands. Resizing preserves the current page and typing progress.


### 输入框大小与结束行程 / Input size and End trip

“聊天输入框大小”仅改变聊天输入界面。原版回复框保持原尺寸，仅在屏幕不足时整体缩小适配。
同行、矿洞护卫或钓鱼期间，对自己的同行 NPC 打开聊天输入界面，顶部会出现“结束行程”。
点击直接结束当前 NPC 的陪同，不调用 AI；普通地点衔接 NPC 日程，无法衔接时使用原有返家逻辑。
NPC 位于地下 MineShaft 地图时，先移到 Mine 入口大厅的安全落点，再交回日程管理，玩家位置不变。
结束记录写入短期共同经历，保留真实到达与鱼获，不把取消当作到达。当前多人支持范围沿用原有主机陪同限制。

Chat input size affects only the composer. Native replies keep their original scale unless the screen requires fitting.
End trip appears for the current player's active travel, mine guard or fishing companion. It ends the session locally,
restores schedule control, and sends underground NPCs to the mine entrance before releasing them. It never teleports the player.
Other players' sessions cannot be ended. Existing host-only companion support remains in effect.

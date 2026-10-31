# 中英文提示词 / Bilingual prompts

提示词模板语言跟随星露谷标题界面的语言选择，无需Mod语言开关。普通聊天的回复跟随玩家本轮输入语言，无法判断时回退游戏语言；其他生成任务继续跟随游戏语言。中文（zh系列）使用`zh/`，英文使用`en/`，其他暂未适配语言回退英文。每次新请求重新读取文件；进行中的请求及等待玩家确认的对话使用发起时的语言。

Prompt template selection follows Stardew Valley. Manual-chat replies follow the player's current input language, falling back to the game language when unclear; other generation tasks still follow the game language. Chinese locales use `zh/`, English and currently unsupported locales use `en/`. Each new request reloads the editable prompts. In-flight requests and pending confirmations keep their original language.

| 文件（在zh/en各一份） | 用途 / Purpose |
| --- | --- |
| `zh/conversation.txt`或`en/conversation.txt` | 普通对话口吻、连续性与行动意愿 / Manual chat voice, continuity and autonomy |
| `zh/conversation-final.txt`或`en/conversation-final.txt` | 工具执行后最终回复 / Final reply after tool execution |
| `weekly-story.txt` | 周剧情题材与创作方向 / Weekly story creative direction |
| `weekly-story-format.txt` | 周剧情格式及完整示例 / Weekly execution schema and complete example |
| `game-role.txt` | 其他场景共用的角色扮演规则 / Shared role rules for other scenes |
| `proactive-scene.txt` | 已安排剧情的主动来访台词 / Scheduled encounter dialogue |
| `social-scene.txt` | 正常行程中的主动相遇 / Spontaneous social encounters |
| `overnight-mail.txt` | 隔夜邮件选择与正文 / Overnight mail planning and text |
| `gift-decision.txt` | 当面礼物决策 / Immediate gift planning |
| `memory-summary.txt` | 长期记忆整理 / Long-term memory compaction |
| `signal-classifier.txt` | 对话后的关系信号提取 / Conversation signal extraction |

`conversation-context.json`仍在根目录并由两种语言共用。其中的WorldFactFields中文键是现有内部筛选标识，不能为了英文模式翻译它们。JSON字段、动作、NPC内部名、地图ID、候选key都保持原值。

`conversation-context.json` is shared. Its Chinese WorldFactFields keys are internal identifiers and must not be translated. Protocol keys, actions, internal NPC/map IDs and candidate keys are language-independent.

## 编辑与升级

- 日常只编辑`zh/`或`en/`下的对应文件，保存为UTF-8；下一次生成生效。两套创作方向可以分别定制，但格式规则必须保持一致。
- 文件缺失或为空时回退同语言内置版本；英文不会回退旧中文提示词。
- 根目录旧的四个txt仅为升级兼容。中文子目录文件缺失时可使用旧文件；子目录仍为内置默认且旧根目录文件有自定义修改时，也保留旧自定义并记录提示。显式修改过的`zh/`文件优先。
- 新安装包不包含根目录旧txt，避免覆盖已有自定义。建议把保留的旧中文修改合并到`zh/`，之后只维护新目录。更新前备份两种语言的自定义提示词。
- 本地界面翻译在`i18n/default.json`（英文）与`i18n/zh.json`（中文），需保留占位符（如`{0}`）。修改界面翻译后重启游戏。
- 已生成、已保存的台词、邮件和剧情不自动翻译或重新生成；旧历史可作为上下文；新输出通常跟随游戏语言，普通聊天则跟随玩家本轮输入语言。不会因切换语言额外请求AI或重置预算。
- 普通聊天的系统提示以 NPC 身份开头，再接人格、好感度与对话规则。`{npc_identity}` 请只保留一处；不要手动再写一遍角色身份。新规则同样用于行动后的最终回复。托管和自定义 API 共用该组装逻辑。
- 编辑旧自定义模板时，请将强制游戏语言的要求改为跟随玩家本轮输入，避免与新的普通聊天语言规则冲突。
- AI可以理解未翻译的旧历史和内部游戏事实；这些内部上下文不应原样作为面向玩家的回复。开发诊断日志和底层错误细节可能保留原语言。

Edit the corresponding file in `zh/` or `en/`. Empty or missing files fall back to the same language's embedded defaults. Legacy root Chinese customizations remain compatible, but new edits should use the language folders. UI translations are in `i18n/default.json` and `i18n/zh.json`; retain placeholders. Back up customized prompts before installing updates. Existing generated content is preserved; switching language never purchases a translation or resets request budgets. Technical logs and external error details may retain their original language.

---

# 普通对话配置入口

普通对话（玩家主动输入文字）在这里管理。保存为 UTF-8。

| 文件 | 用途 |
| --- | --- |
| `zh/conversation.txt`或`en/conversation.txt` | 人格、语气、回复长度、连续性、记忆与行动意愿规则。开头的 `{npc_identity}` 自动填入原版或里奇赛德村的 NPC 身份，不需要写死 NPC 名字；旧自定义模板没有该占位符时，代码在模板前补充身份。 |
| `zh/conversation-final.txt`或`en/conversation-final.txt` | 工具执行后或需要补充生成最终回复时的文字规则。直接完成的普通聊天不经过这个阶段。 |
| `conversation-context.json` | 决定 `world_facts` 发送哪些额外游戏事实。 |

安装本版本 DLL 和 assets 后，编辑**游戏 Mods/VivantValley/assets/prompts/** 下的文件，下一轮普通对话自动读取，无需重新编译或重启游戏。已经开始的对话（含等待确认、重试）继续使用开始时的配置。

源码目录的文件用于下次构建和打包，不会自动覆盖已安装 Mod。更新 Mod 时请保留自己修改的 prompts 文件。

## 精简默认值

```json
{
  "WorldFactFields": ["玩家", "天气/节日", "配偶/室友"]
}
```

技能、社区中心/Joja/影院进度、钥匙能力、姜岛船、任务、医院和战斗清单默认不发送。需要时可将以下字段名加入数组：

`玩家`、`天气/节日`、`配偶/室友`、`技能`、`进度`、`能力`、`姜岛船`、`任务`、`医院`、`战斗`。

空数组 `[]` 表示不发送这些额外事实。字段名必须准确；这只是世界事实白名单，不改变游戏里的技能、任务、关系或行动权限。世界事实仍受 300 字符预算限制。

当前时间、NPC 地点/附近植物、与该 NPC 的关系、近期时间线、相关记忆、已发生的剧情事实是独立上下文字段，本次保留原有行为，不由 WorldFactFields 开关控制。玩家自己提及或历史记忆中已出现的任务/技能，也不会因此被删除。

普通聊天发送顺序为：系统提示（保留 NPC 身份开头与角色微调）→ 只读上下文（当前时间、场景、关系、world_facts、current_activity、行动阶段的移动候选、相关 memory、可选 story_facts、timeline）→ 玩家本轮输入。移动候选仅在行动选择阶段、有候选时提供，位于 memory 之前。最终回复沿用公共字段顺序，省略移动候选，之后追加工具结果或草稿及收尾规则。实时事实优先于历史；不冻结时间、好感度或记忆。timeline 的分段和存档兼容规则见下文。

Manual chat order: system identity/rules → current time, scene, relationship, world facts, current activity, action-only move candidates → relevant memory → experienced story facts (if any) → timeline → separate current player input. Finalization preserves this order, omits move candidates, then appends tool results/draft and final-response rules.

timeline 现在按完整 AI 问答轮次（含本轮已确认的行动结果）和独立历史事件组织。`current_activity` 单独携带原 `RecentSessionFacts` 的临时状态/近期共同经历，不占 timeline 的窗口。历史邀请不代表活动仍在进行；实时状态及工具结果优先。

- 一段窗口最多 8 轮完整 AI 对话、8 组独立事件，且条目正文合计最多 2400 个 .NET 字符（不是 Token，不包含 JSON 包装）。通常仅在末尾追加。
- 超出任一上限时，整组移出旧记录，目标保留最近不超过 4 轮、4 组事件和 1200 字符。至少保留最新一组；特别长的一组使用明确标注的摘录，最多 2400 字符。
- 不再按每条 100 字符截断。正常轮次保留完整原话；单组特别长时保留各条记录的开头和结尾，用省略标记及说明明确它是摘录，不能据省略部分推断同意、承诺或行动成功。原始消息不被摘录覆盖。
- 存档新增可选的消息编号 `TimelineSequence`、轮次关联 `ConversationTurnId`，以及窗口起点 `TimelineStartSequence`、后续编号 `NextTimelineSequence`。读旧档时只初始化这些元数据，不裁剪或重写原消息及长期摘要；相邻、同日期的旧 AI 问答可归为一轮，无法确认归属的事件或残缺对话保留为独立记录。
- 窗口起点随存档和快照复制，读档不把已移出窗口的旧对话重新塞回来。新消息写入时继续执行原有近期原文保留策略，但裁剪对齐完整轮次，并保留仍在活动窗口内的原文。这不是无限聊天归档。
- 长期 memory 的更新、按话题召回及 350 字符召回预算沿用现有策略；本改动不额外请求模型生成摘要，也不把移出窗口的所有对话自动写进长期记忆。

这些是初始长度参数；需要结合对话质量、总输入量、未命中 Token 和实际费用评估。缓存是否命中以供应商返回的 usage 为准，本地公共前缀相同不等于已命中缓存。

## 文件缺失或格式错误

文本为空、文件丢失或 JSON 格式/字段名错误时，会记录 SMAPI 警告并使用对应文件的内置默认值。默认值直接由这套资源文件嵌入 DLL，没有第二份 C# 提示词副本。

## 代码位置

- `Services/ConversationPromptService.cs`：每轮读取文件和处理默认值。
- `Services/ConversationContextBuilder.cs`：普通对话额外事实筛选。
- `Services/InProcessConversationEngine.cs`：组装请求、保留工具协议和输出字段结构。
- `Services/GameContextBuilder.cs`：其他场景共用的完整游戏快照；其中的长篇扮演规则不再作为普通对话的编辑入口。

普通对话配置整理不调整后端 URL、鉴权、请求字段结构、工具名称及参数结构，也不修改主动社交、礼物执行、移动及战斗的游戏侧判断。周剧情的独立扩展见下节。

## 周剧情：多阶段选择与关系冲突

周剧情另用以下两个文件，与上面的普通对话互不影响：

- `weekly-story.txt`：主要编辑入口。题材、人物冲突、吃醋、竞争、摊牌、亲密尺度及结局方向。
- `weekly-story-format.txt`：可执行格式说明。默认两次选择、条件片段、分支引用及动作枚举，附完整11节点示例；改文字不能绕过本地校验器。

文件在每次**生成新周剧情**时读取；修改不改变已经规划好或已发送邀请的剧情。旧存档单选择剧情继续按原规则播放，新生产请求要求formatVersion=2。现有每周生成预算和失败重试次数不变。

建议10～18个节点、两次选择，每次优先两项；完整剧情最多48个节点，不再限定为2～3次选择。普通台词和选项可用when依赖之前选择。最后选择使用“关系靠近”“悬而未决”“保持距离”标签，不必三类齐全，多个具体结局可以共用一个标签。跳过未选中的片段，不跳转、不循环。实际显示并关闭的回应才会记入已完成选择；中断后重新开始会清空本次临时选择。每名NPC在完成时按最后一个涉及自己的选择结算一次好感，不累加每次点击奖励。

创作要求与执行校验分开：

- 心数、恋爱状态、supporting仅作为AI创作背景，不再按固定恋爱等级否决V2剧情；配角也可以发出邀请。
- 提示词注明成人亲密仅适用于明确adult=true的角色，剧情发展尊重玩家实际选择；不新增性行为动画或露骨描写。
- 不再要求三段开场台词、选择之间必须有普通台词、至少一半台词面向玩家或必须写when。选项专属回应本身也可体现分支。
- boundary是叙事记录，不自动隐藏后续选项。只有明确的when控制可见性。
- 两次选择、每次两个选项是短篇建议，不是拒收门槛。V2可有一个或多个选择，超过三个选项用上一页/下一页完整展示；翻页不推进剧情、不记为选择、不结算奖励。
- 保留技术容量：完整剧情最多48节点、单选择最多48项、每条路线至少两个可用选项。条件验证只保留未来会读取的选择状态，合并等价状态，最多4096个有效状态；独立选择不会因组合数膨胀被误拒绝。
- 保留演员/地点、动作枚举、唯一ID、有效引用、可达选项、最终end及实际完成校验。旧V1按原协议兼容，历史RomanceLevel字段保留但不用于V2校验。

`WeeklyStoryAllowMarriedRivalry`仍影响候选背景中的supporting标记，供创作参考，不再作为生成后否决条件；不会直接提升好感或建立恋爱关系。候选筛选及玩家设置的最低心数未改。

`WeeklyStoryMaxOutputTokens`默认4000，上限6000。首次运行本次更新时，旧值2400会一次性迁移到4000，并写入`WeeklyStoryGenerationSettingsVersion: 1`；其他自定义值保留，迁移后手动设回2400也不会再次覆盖。提高的是输出额度，实际用量取决于生成长度；请求地址、鉴权、非流式模式及请求次数预算未改。

第二次请求按失败原因修正：JSON疑似截断时缩为8～12节点；节点过多时缩为10～14节点；JSON语法或类型错误时要求重新输出正确完整对象。不会补造残缺JSON或裁掉分支来冒充成功。每周仍最多两次自动请求，安装更新、读档或跨天不会重置已经耗尽的本周预算；下一周使用新规则。

安装时须同步更新`VivantValley.dll`、`i18n/`及`assets/prompts/zh/`、`assets/prompts/en/`，避免旧提示仍要求恋爱等级或固定选择数量。如果游戏目录另有自定义题材，先备份并将题材合并到对应语言的`weekly-story.txt`，不要把旧等级规则带回。普通对话自定义文件无需覆盖。

技术位置：`WeeklyStoryBranchPolicy`负责条件/可达路径及完成判定；`WeeklyStoryRomancePolicy`只整理已发生的关系回应；`WeeklyStoryRunner`负责播放；选择记录保存在原周剧情存档键中。


玩家性格微调 / Player personality adjustments
-------------------------------------------
`conversation.txt` 中的 `{player_personality}` 位于 NPC 身份开场之后、实时事实与连续性规则之前。
三个字段由对话界面的“NPC 性格微调”编辑，留空不会注入示例。每项最多 240 个 UTF-16 字符，保持完整文字元素。
设置保存在当前玩家的 `modData`，按 NPC 内部名称隔离，随游戏存档保存，与聊天记忆清理独立。
本轮请求开始时固定设置，修改从下一轮手动聊天生效；行动后的最终回复沿用本轮设置。
托管和自定义 API 共用相同逻辑。未带新占位符的旧自定义模板会在开场之后插入设置。

`{player_personality}` inserts the player's non-empty tone, personality tendencies and expression habits
right after the NPC opening. Blank fields do not insert examples. Preferences are per farmer/NPC,
persist with the game save, and apply to the next manual conversation on both hosted and custom APIs.
They are separate from generated memory; an in-flight turn keeps its captured preferences.


## 小镇回声 / Town echoes

默认启用，`config.json` 中的 `EnableTownEchoes: false` 关闭记录、传播和召回，不删除旧记录。

传播在每日开始和读档后自动处理，无距离、地点、碰面、日程、节日或每天接收一条的限制。发生当天不传播；最早次日从亲历者的 5 人名单中稳定抽选 3 人；每个一级接收者最早在获知后的次日，再从自己的名单中选 2 人。二级接收者不再传播。名单重叠、当事人或已知情者被跳过，因此一条事件最多新增 9 个知情者，人数不足不强行补齐。重复读档不重抽、不重复投递，也不会在同一天连传两轮。暂停功能后重新启用时按实际获知日补处理，第二轮仍等一天。

`assets/social/town-echo-contacts.json` 配置每个角色的五个可传播对象（内部 NPC 名，有方向）。内置 36 个角色，名单用于本 MOD 的传播玩法，不声称是原版官方关系。可添加其他 MOD NPC，每项必须是五个不同的其他人物；配置错误时日志提示并回退内置名单。未配置人物首次载入/产生事件时使用五人默认名单，不会删除其事件。无需角色当时在玩家附近。

事件来源：
- 护卫同行时实际与玩家进入同一矿层；钓鱼同行时实际与正在钓鱼的玩家同场。实际经历确认与消息传播分开，不再有距离门槛。
- 玩家实际送给 NPC 的礼物；NPC 实际交付的对话礼物、主动互动礼物；玩家实际领取的邮件附件。拟赠送、未领取邮件不算已收到。
- 已完成主动相遇/来访；礼物婉拒如实标记未交付。同一个主动送礼动作只生成一条事件。
- 完成每周剧情后，只传播实际选择、地点、参与者，不使用规划概要、未选分支或隐藏动机。多演员共用一个事件，邀请人发起首轮，其他演员已经知情。
- 新剧情可明确声明 `activityKind: date`，普通剧情默认 `story`。约会邀请不等于已约会；仅实际完成才记录完成事件。已经读过台词/获得实际回应后中断，另记“约会中断，未完成”，不编造成功结果。旧剧情不凭标题、好感或浪漫选项自动认定为约会。

消息按存档、玩家隔离保存，保留 14 游戏日、最多 128 条事件。保存原始来源、每次转述者、接收者、获知日期和层级。一级接收者能知道自己告诉谁；原始来源不会自动知道下游转述。旧版单轮消息保留原接收日期和来源，补齐首轮额度并按规则允许第二轮。

每轮相关 memory 最多一条消息；保留原个人 memory 的 350 字符额度，额外转述文本最多 900 字符，明确是亲历还是转述及来源链。不插入接收者的亲历 timeline，不修改系统身份开头，不增加 NPC 间 AI 请求。新消息的主动提及机会只在成功完成聊天后消耗；是否实际说出由模型决定，之后相关提问仍可召回。中英文、托管和自定义 API 共用。

控制台：`vivant_echo` 查看事件和实际传播链；`vivant_echo contacts Abigail` 查看某人的五人名单。`vivant_forget <NPC内部名|all>` 清理对应传播记录；删除中间转述者时也清理依赖该来源的二级记录，旧消息不会重新投递。本版沿用主机持久化范围，不新增远程农场助手同步。

Enabled by default. Propagation runs automatically on day start/save load, with no meeting, distance, location, schedule or daily listener cap. First wave: up to three of the source's five configured contacts, starting next day. Each first-hop listener may tell up to two of their own five contacts starting the day after receipt. No third hop; duplicate recipients and witnesses are skipped. Stable choices and saved receipts prevent rerolls on reload. Edit `assets/social/town-echo-contacts.json` using internal NPC names; 36 characters are bundled, unknown characters receive five fallback contacts.

Sources are observed companionship, delivered gifts, claimed mail gifts and completed proactive/weekly scenes. Declines are explicitly undelivered. Weekly stories may explicitly declare `activityKind: date`; invitations, unplayed branches and prospective summaries are never completed events. A date interrupted after observed dialogue is recorded as incomplete. Each listener knows the actual source chain and only their own outgoing transfers. One event per recall, up to 900 additional characters; 14-day/128-event retention and host persistence apply. No NPC-to-NPC model calls. `vivant_echo` and `vivant_echo contacts Abigail` show read-only status. `vivant_forget` also removes dependent echo chains.

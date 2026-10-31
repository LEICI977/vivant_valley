# 分享工作台 / Share studio

电脑版 0.16.0。在聊天输入框或已完成的回复中点击“分享 / Share”进入工作台。图片在本地生成，不调用 AI，不消耗额度。

## 使用

1. **对话**：选择当前 NPC 最近五轮完整聊天。默认选中最后一轮，支持全选、展开查看。内部工具结果、提示词、未完成回复不会进入分享。
2. **模板**：选择七套模板之一：鹈鹕镇聊天窗、春日手账、雨夜私语、今日份心动、夜色偏爱、嘴硬心软、脸红日记。缩略图为设计示例；实际输出读取所选真实聊天和当前人物。
3. **文字**：编辑主标题与副标题。主标题支持 Enter 换行；留空隐藏。切换模板保留自定义文字，“恢复模板默认文字”才重置。长标题会提示精简；聊天正文过长时自动分页。可从原话候选中选一句作标题。
4. **头像**：默认使用当前 NPC 的游戏肖像与玩家当前外观。在“文字”页可使用本地 NPC 图片或恢复游戏头像。Windows 文件选择器支持 PNG/JPEG/WebP，最大 12 MB；当前版本本地图片选择器限 Windows。
5. **背景**：模板默认、全部、通用、当前角色相关分类；搜索名字、类别或人物。点击分类展开选项。使用箭头调整等比裁切位置。人物限定背景中的已有画像是背景的一部分，不会因替换头像而改变。背景保持原色，局部纸张和气泡保证文字可读。
6. **贴纸**：点击素材添加，拖动移动。选中后显示缩放、旋转、翻转、图层与删除工具。画布上滚轮缩放选中贴纸。Delete 删除。最多 50 张贴纸，清空按钮只清空当前页。
7. **比例**：顶部或“文字”页选择 3:4 / 4:5 / 1:1 / 9:16，对应 1440×1920 / 1440×1800 / 1440×1440 / 1440×2560。切换后重新排版，不拉伸整图、不截掉原文。
8. **预览与导出**：底部缩略图或左右箭头翻页。放大后拖动画布查看；收起工具可扩大预览。导出全部 PNG 后点击“打开保存目录”。文件位于 Mod 的 shares/日期时间-随机编号/share-01.png 等，不覆盖旧图片。

“今日份心动”默认导出封面与完整聊天内页。在“对话”页选择封面原句，也可以把摘录框删减为已选回复中的连续原文片段；超长或不是原文的摘录会提示修正。关闭封面可直接输出聊天内页。

Ctrl+Z / Ctrl+Y 撤销、重做主要编辑。Esc 在输入框中先结束输入，再按一次返回聊天。当前草稿仅在本次工作台会话内保留；导出图片永久保存在磁盘。正文选择变化导致分页变化后，请检查各页贴纸。撤销可恢复修改前状态。

## 渲染与资源

文字由游戏自带 SkiaSharp 在目标分辨率抗锯齿绘制，优先使用系统微软雅黑、苹方、Noto 字体，不打包系统字体。像素头像和贴纸保持像素风格。预览与 PNG 共用排版与绘制代码。

素材索引：assets/share/catalog.json。模板缩略图：assets/share/templates/。原图来自用户提供的背景库；贴纸来源及许可保留在 assets/share/stickers-*。运行时不依赖桌面素材路径、设计 HTML 或 Python。图片按需缓存，关闭菜单释放工作台资源，不卸载游戏共享头像。

## English

Choose Share in a completed conversation. Use Chat, Style, Text, Paper and Art to select turns, a template, editable titles, backgrounds and stickers. All seven styles use the current NPC. Choose 3:4, 4:5, 1:1 or 9:16; long messages continue across pages without losing content. The cover style can export an original quote plus full conversation pages. PNG export is local and does not call AI. Ctrl+Z / Ctrl+Y undo and redo; mouse wheel scrolls the tool panel or resizes a selected sticker. The native local portrait picker currently supports Windows. Drafts last for the editor session; exported PNGs remain under shares/.

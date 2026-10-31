using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class ShareCardMenu
{
    private static readonly Color UiInk = Hex("344A3C"), UiMuted = Hex("819080"), UiAccent = Hex("41634D"), UiPaper = Hex("FAF8F1");
    private int panelContentHeight;
    private bool categoriesExpanded;
    private Rectangle scrollTrack;
    private int quotePanelOffset;
    private void UiText(SpriteBatch b, string value, int x, int y, float size = 17, int width = 1000, Color? color = null, bool bold = false)
        => uiText.Draw(b, uiText.Fit(value, size, Math.Max(1, width), bold), new Vector2(x, y), size, color ?? UiInk, 1, bold);
    private void Button(SpriteBatch b, Rectangle r, string label, Action click, bool active = false, bool enabled = true, bool primary = false)
    {
        bool hover = false;
        Color fill = primary ? UiAccent : active ? Hex("E0E9D8") : hover ? Hex("EEEFE3") : Hex("F4F2E9");
        DrawRoundedRect(b, r, 8, enabled ? fill : Hex("E5E4DB"));
        float size = r.Width < 78 ? 14 : 16;
        int padding = r.Width < 78 ? 5 : 12;
        UiText(b, label, r.X + padding, r.Y + (r.Height - 24) / 2, size, r.Width - padding * 2,
            !enabled ? Hex("A9AC9E") : primary ? Hex("FFF9EB") : UiInk, primary || active);
        if (enabled) buttons.Add((r, label, click));
    }
    private void SetTab(string value)
    {
        tab = value; panelScroll = assetPage = 0; category = ""; categoriesExpanded = false; search.Text = ""; lastSearch = ""; collapsed = false;
        foreach (var input in Inputs) input.Selected = false;
        Game1.keyboardDispatcher.Subscriber = null;
    }
    public override void draw(SpriteBatch b) => DrawMobileStudio(b);
    private void DrawPanel(SpriteBatch b)
    {
        int left = panel.X + 16, width = panel.Width - 32, top = panel.Y + 68, bottom = panel.Bottom - 14;
        int y = top - panelScroll;
        bool Visible(int py, int height) => py >= top && py + height <= bottom;
        void Text(string value, int height = 30, float size = 17, Color? color = null, bool bold = false)
        { if (Visible(y, height)) UiText(b, value, left, y, size, width, color, bold); y += height; }
        void Btn(string label, Action click, bool active = false, bool enabled = true, int height = 37)
        { height = Math.Max(48, height); if (Visible(y, height)) Button(b, new Rectangle(left, y, width, height), label, click, active, enabled); y += height + 9; }
        void Input(ShareStudioInput input, int height)
        { height = Math.Clamp(height, 48, Math.Max(48, bottom - top)); if (Visible(y, height)) { input.Bounds = new Rectangle(left, y, width, height); input.Draw(b, uiText, (r, c, radius) => DrawRoundedRect(b, r, radius, c)); } y += height + 13; }
        if (tab == "templates")
        {
            Text(T("选择一种喜欢的感觉", "Choose a feeling"), 28, 18, bold: true);
            Text(T("模板可搭配任意 NPC", "Every style works with your NPC"), 27, 13, UiMuted);
            int cellWidth = (width - 10) / 2, cellHeight = Math.Min(172, Math.Max(48, bottom - top));
            for (int i = 0; i < ShareTemplate.All.Length; i++)
            {
                var t = ShareTemplate.All[i]; int rowY = y + (i / 2) * (cellHeight + 12);
                var r = new Rectangle(left + i % 2 * (cellWidth + 10), rowY, cellWidth, cellHeight);
                if (!Visible(rowY, cellHeight)) continue;
                DrawRoundedRect(b, r, 9, t.Id == template.Id ? Hex("DEE8D5") : Hex("EEECE3"));
                var thumbnail = TemplateThumbnail(t.Id);
                if (thumbnail is not null)
                {
                    float fit = Math.Min((r.Width - 16f) / thumbnail.Width, (cellHeight - 48f) / thumbnail.Height);
                    b.Draw(thumbnail, new Rectangle(r.Center.X - (int)(thumbnail.Width * fit) / 2, r.Y + 8, (int)(thumbnail.Width * fit), (int)(thumbnail.Height * fit)), Color.White);
                }
                UiText(b, T(t.Chinese, t.English), r.X + 9, r.Bottom - 30, 14, r.Width - 18, bold: t.Id == template.Id);
                buttons.Add((r, T(t.Chinese, t.English), () => ChangeTemplate(t.Id)));
            }
            y += ((ShareTemplate.All.Length + 1) / 2) * (cellHeight + 12);
            Text(T("缩略图为效果示例，预览使用当前头像", "Thumbnails are examples; preview uses your NPC"), 40, 12, UiMuted);
        }
        else if (tab == "turns")
        {
            Text(T("最近五轮对话", "Recent five turns"), 32, 20, bold: true);
            Btn(selected.All(v => v) ? T("取消全选", "Clear selection") : T("选择全部", "Select all"), () => { Checkpoint(); Array.Fill(selected, !selected.All(v => v)); Reflow(); });
            for (int i = 0; i < turns.Count; i++)
            {
                int index = i; bool expanded = expandedTurn == i;
                var previewLines = ShareCardModel.Wrap(T("我：", "Me: ") + turns[i].Player + "\n" + npcName + ": " + turns[i].Reply, s => uiText.Measure(s, 15), width - 28).ToArray();
                int lines = expanded ? previewLines.Length : Math.Min(4, previewLines.Length);
                int height = 69 + lines * 23;
                if (Visible(y, 48)) Button(b, new Rectangle(left, y, width - 63, 48), (selected[i] ? "✓ " : "+ ") + T($"第 {i + 1} 轮", $"Turn {i + 1}"),
                    () => { Checkpoint(); selected[index] = !selected[index]; Reflow(); }, selected[i]);
                if (Visible(y, 48)) Button(b, new Rectangle(left + width - 58, y, 58, 48), expanded ? "−" : "…", () => expandedTurn = expanded ? -1 : index);
                for (int li = 0; li < lines; li++)
                { int ly = y + 56 + li * 23; if (Visible(ly, 23)) UiText(b, previewLines[li], left + 8, ly, 15, width - 16, li == 0 ? UiInk : UiMuted); }
                y += height + 15;
            }
            if (template.Id == "04")
            {
                Btn(coverEnabled ? T("封面＋聊天内页：开启", "Cover + conversation: on") : T("封面：关闭", "Cover: off"), () => { Checkpoint(); coverEnabled = !coverEnabled; Reflow(); }, coverEnabled);
                Text(T("封面摘录 · 可删减为原文片段", "Cover quote · keep an original excerpt"), 30, 14, UiMuted);
                Input(excerpt, 108);
                Text(T("选择封面原句", "Choose a quote"), 32, 18, bold: true);
                foreach (string candidate in QuoteOptions().Distinct())
                { string text = candidate; Btn(text, () => { Checkpoint(); quote = text; quoteEdited = false; Reflow(); }, quote == text, height: 48); }
            }
        }
        else if (tab == "text")
        {
            Text(T("写下你的标题", "Make it yours"), 34, 20, bold: true);
            Text(T("大标题 · 换行或留空隐藏", "Headline · newline or leave empty"), 27, 14, UiMuted); Input(title, 87);
            Text(T("副标题 · 可选", "Subtitle · optional"), 27, 14, UiMuted); Input(subtitle, 43);
            Btn(T("从对话中选一句作标题", "Use a line from the chat"), () => { panelScroll = quotePanelOffset; });
            Btn(T("恢复模板默认文字", "Restore template wording"), () => { Checkpoint(); titleCustom = subtitleCustom = false; title.Text = T(template.TitleZh, template.TitleEn); subtitle.Text = T(template.SubtitleZh, template.SubtitleEn); lastTitle = title.Text; lastSubtitle = subtitle.Text; Reflow(); });
            Text(T("角色头像", "NPC portrait"), 32, 18, bold: true);
            Btn(T("选择本地头像 / 刷新", "Local portraits / refresh"), ChoosePortrait);
            if (browsingPortraits)
            {
                Text(T("MOD/share-portraits · PNG/JPG", "MOD/share-portraits · PNG/JPG"), 30, 14);
                if (portraitFiles.Length == 0) Text(T("文件夹暂无图片", "No images in this folder"));
                foreach (string path in portraitFiles)
                    Btn(Path.GetFileName(path), () =>
                    {
                        var image = LoadCustomPortrait(path);
                        if (image is null) return;
                        Checkpoint(); portraitPath = path; customAvatar = image; Invalidate();
                    }, portraitPath == path);
                Btn(T("收起头像列表", "Hide portrait list"), () => browsingPortraits = false);
            }
            Btn(T("恢复游戏头像", "Use game portrait"), () => { Checkpoint(); portraitPath = null; customAvatar = null; Invalidate(); }, portraitPath is null);
            Btn(hideName ? T("玩家姓名：隐藏", "Player name: hidden") : T("玩家姓名：显示", "Player name: shown"), () => { Checkpoint(); hideName = !hideName; Invalidate(); });
            Text(T("画布比例", "Canvas ratio"), 30, 18, bold: true);
            foreach (var r in ShareRatio.All) Btn(r.Id + $"   {CardWidth * 2} × {r.Height * 2}", () => ChangeRatio(r.Id), ratio.Id == r.Id);
            quotePanelOffset = y + panelScroll - top;
            Text(T("原话标题候选", "Original lines for your title"), 32, 18, bold: true);
            foreach (string candidate in QuoteOptions().Distinct())
            { string text = candidate; Btn(text, () => { Checkpoint(); title.Text = text; titleCustom = true; lastTitle = title.Text; Reflow(); panelScroll = 0; }, height: 45); }
        }
        else
        {
            bool stickersTab = tab == "stickers";
            Text(stickersTab ? T("一点装饰，一点心动", "Little finishing touches") : T("挑一张喜欢的底色", "Choose your backdrop"), 33, 19, bold: true);
            if (stickersTab && selectedSticker >= 0)
            {
                Btn(T("缩小", "Smaller"), () => ChangeSticker(s => s with { Size = Math.Max(24, s.Size - 16) }));
                Btn(T("放大", "Larger"), () => ChangeSticker(s => s with { Size = Math.Min(420, s.Size + 16) }));
                Btn(T("旋转 15°", "Rotate 15°"), () => ChangeSticker(s => s with { Rotation = s.Rotation + MathHelper.Pi / 12 }));
                Btn(T("水平翻转", "Flip horizontally"), () => ChangeSticker(s => s with { Flipped = !s.Flipped }));
                Btn(T("上移一层", "Bring forward"), () => Layer(1)); Btn(T("下移一层", "Send backward"), () => Layer(-1));
                Btn(T("删除选中贴纸", "Delete selected sticker"), RemoveSticker);
                Btn(T("完成编辑，继续加贴纸", "Done; browse stickers"), () => selectedSticker = -1);
            }
            if (!stickersTab)
            {
                Btn(T("使用模板默认背景", "Use template backdrop"), () => { Checkpoint(); background = null; backgroundX = backgroundY = .5f; Invalidate(); }, background is null);
                foreach (var filter in new[] { ("all", T("全部背景", "All backdrops")), ("general", T("通用背景", "General")), ("npc", T("当前角色相关", "Current NPC")) })
                { var f = filter; Btn(f.Item2, () => { backgroundFilter = f.Item1; assetPage = 0; }, backgroundFilter == f.Item1, height: 31); }
                if (background is not null)
                {
                    Text(T("裁切位置", "Crop position"), 27, 14, UiMuted);
                    int by = y;
                    string[] names = { "←", "↑", "↓", "→", "↺" };
                    for (int i = 0; i < 5; i++)
                    {
                        int index = i; if (Visible(by, 48)) Button(b, new Rectangle(left + i * (width / 5), by, width / 5 - 3, 48), names[i], () =>
                        { Checkpoint(); if (index == 0) backgroundX = Math.Max(0, backgroundX - .1f); if (index == 3) backgroundX = Math.Min(1, backgroundX + .1f); if (index == 1) backgroundY = Math.Max(0, backgroundY - .1f); if (index == 2) backgroundY = Math.Min(1, backgroundY + .1f); if (index == 4) backgroundX = backgroundY = .5f; Invalidate(); });
                    }
                    y += 57;
                }
            }
            Text(T("搜索素材或分类", "Search artwork or category"), 27, 14, UiMuted); Input(search, 41);
            string kind = stickersTab ? "sticker" : "background";
            var categories = new[] { "" }.Concat(assets.Where(a => a.Kind == kind).Select(a => a.Category).Distinct()).ToArray();
            Btn(T("分类：", "Category: ") + (category.Length == 0 ? T("全部", "All") : CategoryName(category)) + (categoriesExpanded ? "  −" : "  +"), () => categoriesExpanded = !categoriesExpanded);
            if (categoriesExpanded)
            {
                int cx = left;
                foreach (string c in categories)
                {
                    string value = c; string label = c.Length == 0 ? T("全部", "All") : CategoryName(c);
                    int cw = Math.Min(width, (int)uiText.Measure(label, 16) + 26);
                    if (cx + cw > left + width) { cx = left; y += 53; }
                    if (Visible(y, 48)) Button(b, new Rectangle(cx, y, cw, 48), label, () => { category = value; assetPage = 0; categoriesExpanded = false; panelScroll = 0; }, category == value);
                    cx += cw + 5;
                }
                y += 57;
            }
            var filtered = assets.Where(a => a.Kind == kind && (category.Length == 0 || a.Category == category)
                && (a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || a.Category.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || a.People?.Any(p => p.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) == true)
                && (stickersTab || backgroundFilter == "all" || backgroundFilter == "general" && (a.People is null || a.People.Length == 0) || backgroundFilter == "npc" && a.People?.Contains(npcId, StringComparer.OrdinalIgnoreCase) == true)).ToArray();
            const int perPage = 12; int maxPage = Math.Max(0, (filtered.Length - 1) / perPage); assetPage = Math.Clamp(assetPage, 0, maxPage);
            Text(T($"{filtered.Length} 个素材 · {assetPage + 1}/{maxPage + 1}", $"{filtered.Length} items · {assetPage + 1}/{maxPage + 1}"), 27, 13, UiMuted);
            if (Visible(y, 48))
            {
                Button(b, new Rectangle(left, y, width / 2 - 4, 48), T("上一页", "Previous"), () => assetPage--, enabled: assetPage > 0);
                Button(b, new Rectangle(left + width / 2 + 4, y, width / 2 - 4, 48), T("下一页", "Next"), () => assetPage++, enabled: assetPage < maxPage);
            }
            y += 57;
            int columns = stickersTab ? 3 : 2, cell = Math.Min((width - (columns - 1) * 8) / columns, Math.Max(16, bottom - top - 27));
            foreach (var item in filtered.Skip(assetPage * perPage).Take(perPage).Select((a, i) => (a, i)))
            {
                int cy = y + item.i / columns * (cell + 33), x = left + item.i % columns * (cell + 8);
                if (!Visible(cy, cell + 27)) continue;
                var r = new Rectangle(x, cy, cell, cell + 27); var a = item.a;
                DrawRoundedRect(b, r, 6, background?.Id == a.Id ? Hex("DBE6CF") : Hex("EEEBDF"));
                if (Texture(a.Thumbnail) is Texture2D tex)
                { float fit = Math.Min((cell - 10f) / tex.Width, (cell - 8f) / tex.Height); b.Draw(tex, new Rectangle(r.Center.X - (int)(tex.Width * fit) / 2, cy + 4, (int)(tex.Width * fit), (int)(tex.Height * fit)), Color.White); }
                UiText(b, a.Name, x + 5, cy + cell + 3, 11, cell - 10);
                buttons.Add((r, a.Name, () =>
                {
                    if (a.Kind == "background") { Checkpoint(); background = a; backgroundX = backgroundY = .5f; status = a.People?.Length > 0 ? T("人物限定背景：", "Character wallpaper: ") + string.Join(", ", a.People) : ""; Invalidate(); }
                    else { if (stickers.Count >= 50) { status = T("最多 50 张贴纸", "Maximum 50 stickers"); return; } Checkpoint(); stickers.Add(new ShareSticker(a.Id, page, 360, CardHeight - 150)); selectedSticker = stickers.Count - 1; panelScroll = 0; Invalidate(); }
                }));
            }
            y += ((Math.Min(perPage, Math.Max(1, filtered.Length - assetPage * perPage)) + columns - 1) / columns) * (cell + 33);
            Btn(T("上一页素材", "Previous artwork"), () => assetPage--, enabled: assetPage > 0);
            Btn(T("下一页素材", "Next artwork"), () => assetPage++, enabled: assetPage < maxPage);
            if (stickersTab) Btn(T("清空当前页贴纸", "Clear stickers on this page"), () => { Checkpoint(); stickers.RemoveAll(s => s.Page == page); selectedSticker = -1; Invalidate(); }, enabled: stickers.Any(s => s.Page == page));
        }
        panelContentHeight = y + panelScroll - top;
        panelScroll = Math.Clamp(panelScroll, 0, Math.Max(0, panelContentHeight - (bottom - top) + 15));
        if (panelContentHeight > bottom - top)
        {
            scrollTrack = new Rectangle(panel.Right - 14, top, 12, bottom - top);
            int thumbHeight = Math.Max(28, (bottom - top) * (bottom - top) / panelContentHeight);
            int thumbY = top + (bottom - top - thumbHeight) * panelScroll / Math.Max(1, panelContentHeight - (bottom - top) + 15);
            DrawRoundedRect(b, new Rectangle(panel.Right - 7, thumbY, 3, thumbHeight), 1, Hex("BDC6B3"));
        }
    }
    private static string CategoryName(string value)
    {
        if (!L.IsChinese) return value;
        return value switch
        {
            "Portrait" => "人物限定", "Pattern" => "通用图案", "villagers" => "村民", "animals" => "动物", "artifacts" => "古物",
            "artisan-goods" => "工匠物品", "artisanGoods" => "工匠物品", "buildings" => "建筑", "cooking" => "料理", "crops" => "作物",
            "fish" => "鱼类", "flowers" => "花卉", "foraging" => "采集", "fruits" => "水果", "gems" => "宝石", "minerals" => "矿石",
            "tools" => "工具", "weapons" => "武器", "achievements" => "成就", "seeds" => "种子", "hats" => "帽子", "furniture" => "家具",
            "bundles" => "献祭包", "clothing" => "服装", "concessions" => "零食", "craftable" => "制造品", "footwear" => "鞋靴",
            "forageables" => "采集物", "locations" => "地点", "lost-books" => "遗失书籍", "maps" => "地图", "medical-supplies" => "补给",
            "misc" => "其他", "mixed-seeds" => "混合种子", "monsters" => "怪物", "rings" => "戒指", "scarecrows" => "稻草人",
            "seasons" => "季节", "shop" => "商店", "skills" => "技能", "special-items" => "特别物品", "trees" => "树木", "trinkets" => "饰品", "weather" => "天气",
            _ => value
        };
    }
    private Texture2D? TemplateThumbnail(string id)
    {
        string path = "templates/" + id + ".png";
        return Texture(path);
    }
    private void SelectPage(int target) { page = Math.Clamp(target, 0, pages.Count - 1); selectedSticker = -1; dirty = true; panOffset = Vector2.Zero; }
    private Vector2 ToCard(int x, int y) => new((x - canvas.X) * CardWidth / (float)Math.Max(1, canvas.Width), (y - canvas.Y) * CardHeight / (float)Math.Max(1, canvas.Height));
}

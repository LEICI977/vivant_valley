using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class ShareCardMenu
{
    private bool browsingPortraits, movingSticker, movingCanvas;
    private string[] portraitFiles = Array.Empty<string>();
    private Vector2 touchOffset, initialPan;
    private void DrawMobileStudio(SpriteBatch b)
    {
        buttons.Clear(); scrollTrack = Rectangle.Empty;
        foreach (var input in Inputs) input.Bounds = Rectangle.Empty;
        var layout = MobileSharePolicy.Layout(MobileRuntime.SafeBounds, MobileRuntime.ControlHeight);
        var header = layout.Header; var footer = layout.Footer; int h = layout.ControlHeight;
        b.Draw(Game1.staminaRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Hex("EAEAE2"));
        int width = (header.Width - 16) / 3;
        Button(b, new(header.X, header.Y, width, h), T("返回聊天", "Back"), Leave);
        Button(b, new(header.X + width + 8, header.Y, width, h), collapsed ? T("编辑工具", "Edit") : T("预览图片", "Preview"),
            () => { collapsed = !collapsed; foreach (var input in Inputs) input.Selected = false; });
        Button(b, new(header.Right - width, header.Y, width, h), T("导出 PNG", "Export PNG"), () => exportRequested = true,
            enabled: selected.Any(s => s) && layoutError.Length == 0, primary: true);
        if (!collapsed)
        {
            // One full-width tool page keeps fields and gestures usable on phone-sized viewports.
            panel = MobileLayout.Center(layout.Content, 650); stage = canvas = Rectangle.Empty;
            DrawRoundedRect(b, panel, 12, UiPaper);
            tabArea = new(panel.X + 8, panel.Y + 8, panel.Width - 16, 48);
            string[] ids = { "turns", "templates", "text", "background", "stickers" };
            string[] labels = { T("对话", "Chat"), T("模板", "Style"), T("文字", "Text"), T("背景", "Paper"), T("贴纸", "Art") };
            int tw = tabArea.Width / 5;
            for (int i = 0; i < ids.Length; i++)
            { string id = ids[i]; Button(b, new(tabArea.X + i * tw, tabArea.Y, tw - 3, 48), labels[i], () => SetTab(id), tab == id); }
            DrawPanel(b);
        }
        else
        {
            panel = Rectangle.Empty;
            stage = layout.Content;
            float fit = Math.Max(.01f, Math.Min((stage.Width - 12f) / CardWidth, (stage.Height - 8f) / CardHeight)) * (zoomed ? 1.7f : 1);
            int cw = (int)(CardWidth * fit), ch = (int)(CardHeight * fit);
            canvas = new(stage.Center.X - cw / 2 + (int)panOffset.X, stage.Center.Y - ch / 2 + (int)panOffset.Y, cw, ch);
            var visible = Rectangle.Intersect(canvas, stage);
            if (preview is not null && !preview.IsDisposed && visible.Width > 0 && visible.Height > 0)
            {
                float sx = preview.Width / (float)Math.Max(1, canvas.Width), sy = preview.Height / (float)Math.Max(1, canvas.Height);
                var source = Rectangle.Intersect(preview.Bounds, new Rectangle((int)((visible.X - canvas.X) * sx), (int)((visible.Y - canvas.Y) * sy),
                    Math.Max(1, (int)(visible.Width * sx)), Math.Max(1, (int)(visible.Height * sy))));
                b.Draw(preview, visible, source, Color.White);
            }
            if (selectedSticker >= 0 && selectedSticker < stickers.Count && stickers[selectedSticker].Page == page)
            {
                var s = stickers[selectedSticker]; int size = (int)(s.Size * fit);
                var box = Rectangle.Intersect(stage, new Rectangle(canvas.X + (int)(s.X * fit) - size / 2, canvas.Y + (int)(s.Y * fit) - size / 2, size, size));
                if (box.Width > 0 && box.Height > 0)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, box.Width, 2), UiAccent);
                    b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Bottom - 2, box.Width, 2), UiAccent);
                    b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, 2, box.Height), UiAccent);
                    b.Draw(Game1.staminaRect, new Rectangle(box.Right - 2, box.Y, 2, box.Height), UiAccent);
                }
            }
        }
        string info = layoutError.Length > 0 ? layoutError : status.Length > 0 ? status
            : T($"{page + 1}/{pages.Count} 页 · 拖动工具页滚动，预览中拖动贴纸", $"{page + 1}/{pages.Count} · Swipe tools; drag stickers in preview");
        UiText(b, info, footer.X, footer.Y - 25, 15, footer.Width, layoutError.Length > 0 ? Hex("B34A50") : UiInk);
        int fw = (footer.Width - 24) / 4;
        if (collapsed)
        {
            Button(b, new(footer.X, footer.Y, fw, h), T("上一页", "Previous"), () => SelectPage(page - 1), enabled: page > 0);
            Button(b, new(footer.X + fw + 8, footer.Y, fw, h), T("下一页", "Next"), () => SelectPage(page + 1), enabled: page < pages.Count - 1);
            Button(b, new(footer.X + (fw + 8) * 2, footer.Y, fw, h), zoomed ? T("适应", "Fit") : T("放大", "Zoom"), () => { zoomed = !zoomed; panOffset = Vector2.Zero; });
        }
        else
        {
            Button(b, new(footer.X, footer.Y, fw, h), T("撤销", "Undo"), () => History(false), enabled: undo.Count > 0);
            Button(b, new(footer.X + fw + 8, footer.Y, fw, h), T("重做", "Redo"), () => History(true), enabled: redo.Count > 0);
            Button(b, new(footer.X + (fw + 8) * 2, footer.Y, fw, h), ratio.Id, () => ChangeRatio(ShareRatio.All[(Array.IndexOf(ShareRatio.All, ratio) + 1) % ShareRatio.All.Length].Id));
        }
        Button(b, new(footer.Right - fw, footer.Y, fw, h), T("保存位置", "Location"), OpenFolder);
    }
    protected override void OnTap(int x, int y, bool playSound)
    {
        if (MobileRuntime.FinishEditing(this, () => OnTap(x, y, playSound))) return;
        var field = Inputs.FirstOrDefault(i => !i.Bounds.IsEmpty && i.Bounds.Contains(x, y));
        foreach (var input in Inputs) input.Selected = ReferenceEquals(input, field);
        if (field is not null)
        {
            lastTextCheckpoint = DateTime.MinValue; field.SelectAt(x, y, uiText);
            MobileRuntime.Edit(this, T("编辑分享文字", "Edit share text"), field.Text, field.Limit, false, field.ApplyNative,
                bounds: () => field.Bounds, caretIndex: field.CaretIndex, multiline: field.Multiline);
            return;
        }
        foreach (var button in buttons.ToArray()) if (button.Bounds.Contains(x, y)) { button.Click(); return; }
        if (collapsed && stage.Contains(x, y))
        {
            selectedSticker = HitSticker(new(x, y));
            if (selectedSticker >= 0) { tab = "stickers"; panelScroll = 0; status = T("拖动移动贴纸；打开编辑工具可缩放、旋转或删除", "Drag to move; open Edit to resize, rotate or delete"); }
        }
    }
    private int HitSticker(Vector2 screen)
    {
        if (!stage.Contains(screen) || !canvas.Contains(screen)) return -1;
        Vector2 point = ToCard((int)screen.X, (int)screen.Y);
        for (int i = stickers.Count - 1; i >= 0; i--)
        {
            var s = stickers[i]; if (s.Page != page) continue;
            Vector2 local = Vector2.Transform(point - new Vector2(s.X, s.Y), Matrix.CreateRotationZ(-s.Rotation));
            if (Math.Abs(local.X) <= s.Size / 2 && Math.Abs(local.Y) <= s.Size / 2) return i;
        }
        return -1;
    }
    protected override void OnContactPressed(Vector2 point) { movingSticker = movingCanvas = false; initialPan = panOffset; }
    protected override void OnContactMoved(Vector2 start, Vector2 current, bool dragged)
    {
        if (!dragged || !collapsed || !stage.Contains(start)) return;
        if (!movingSticker && !movingCanvas)
        {
            selectedSticker = HitSticker(start);
            if (selectedSticker >= 0)
            {
                Checkpoint(); movingSticker = true; tab = "stickers"; panelScroll = 0;
                var s = stickers[selectedSticker]; touchOffset = new Vector2(s.X, s.Y) - ToCard((int)start.X, (int)start.Y);
            }
            else movingCanvas = true;
        }
        if (movingSticker)
        {
            stickers[selectedSticker] = MobileSharePolicy.MoveSticker(stickers[selectedSticker], ToCard((int)current.X, (int)current.Y) + touchOffset, CardWidth, CardHeight);
            Invalidate();
        }
        else if (movingCanvas && zoomed)
        {
            panOffset = initialPan + current - start;
            panOffset.X = Math.Clamp(panOffset.X, -canvas.Width / 2f, canvas.Width / 2f);
            panOffset.Y = Math.Clamp(panOffset.Y, -canvas.Height / 2f, canvas.Height / 2f);
        }
    }
    protected override void OnDrag(Vector2 start, float amount)
    {
        if (!collapsed && panel.Contains(start) && start.Y >= panel.Y + 68 && !MobileRuntime.KeyboardOpen)
            panelScroll = (int)MobileLayout.ClampScroll(panelScroll + amount, panelContentHeight + 15, Math.Max(1, panel.Height - 82));
    }
    public override void receiveScrollWheelAction(int direction)
    { if (!collapsed) panelScroll = (int)MobileLayout.ClampScroll(panelScroll - Math.Sign(direction) * 70, panelContentHeight + 15, Math.Max(1, panel.Height - 82)); }
    public override void receiveKeyPress(Keys key)
    {
        if (key != Keys.Escape) return;
        if (MobileRuntime.DismissKeyboard(this)) return;
        Leave();
    }
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    { buttons.Clear(); panOffset = Vector2.Zero; movingSticker = movingCanvas = false; }
}

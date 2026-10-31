using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;

namespace VivantValley.Menus;

/// <summary>Local share studio. All preview and export paths use the same layout.</summary>
public sealed partial class ShareCardMenu : IClickableMenu, IDisposable
{
    private const int CardWidth = 720;
    private int CardHeight => ratio.Height;
    private readonly string assetRoot, exportRoot, npcName, playerName, npcId;
    private readonly Action returnToChat;
    private readonly Action<Exception> logError;
    private readonly IReadOnlyList<ShareTurn> turns;
    private readonly IReadOnlyList<ShareAsset> assets;
    private readonly Dictionary<string, ShareAsset> byId;
    private readonly bool[] selected;
    private readonly ShareStudioInput title = new() { Multiline = true, Limit = 160 };
    private readonly ShareStudioInput subtitle = new() { Limit = 160 };
    private readonly ShareStudioInput excerpt = new() { Multiline = true, Limit = 500 };
    private readonly ShareStudioInput search = new() { Limit = 100 };
    private readonly List<(Rectangle Bounds, string Label, Action Click)> buttons = new();
    private readonly Dictionary<string, Texture2D> textures = new();
    private readonly Queue<string> textureOrder = new();
    private readonly HashSet<string> failedTextures = new();
    private long textureBytes;
    private readonly ShareCardTextRenderer cardText = new();
    private readonly ShareCardTextRenderer uiText = new();
    private ShareTemplate template = ShareTemplate.All[0];
    private ShareRatio ratio = ShareRatio.All[0];
    private List<ShareStudioPage> pages = new();
    private List<ShareSticker> stickers = new();
    private ShareAsset? background;
    private Texture2D? npcAvatar, customAvatar;
    private string? portraitPath;
    private readonly Dictionary<string, Texture2D> customPortraits = new();
    private readonly ConcurrentQueue<string> pickedPortraits = new();
    private bool pickingPortrait;
    private RenderTarget2D? preview;
    private readonly Dictionary<int, RenderTarget2D> pagePreviews = new();
    private readonly Dictionary<string, Texture2D> templatePreviews = new();
    private Rectangle canvas, stage, panel, tabArea;
    private int page, selectedSticker = -1, assetPage, panelScroll, expandedTurn = -1;
    private string tab = "templates", category = "", backgroundFilter = "all", status = "", lastTitle = "", lastSubtitle = "", lastSearch = "", quote = "";
    private bool hideName = true, coverEnabled = true, dirty = true, disposed, dragging, panning, exportRequested, playerAvatarFailed, titleCustom, subtitleCustom, zoomed, collapsed;
    private float textOutputScale = 1, backgroundX = .5f, backgroundY = .5f;
    private Vector2 dragOffset, panOffset;
    private string? lastExport;
    private string layoutError = "";
    private bool quoteEdited;
    private string lastExcerpt = "";
    private DateTime lastTextCheckpoint = DateTime.MinValue;
    private static string T(string zh, string en) => L.IsChinese ? zh : en;
    private sealed record Snapshot(string Template, string Ratio, string Title, string Subtitle, bool TitleCustom, bool SubtitleCustom,
        string? Background, float BackgroundX, float BackgroundY, bool[] Selected, ShareSticker[] Stickers,
        bool HideName, bool Cover, string Quote, bool QuoteEdited, string? Portrait, int Page);
    private readonly Stack<Snapshot> undo = new(), redo = new();

    public ShareCardMenu(string modDirectory, string npcName, string playerName, IReadOnlyList<ShareTurn> turns,
        Action returnToChat, Action<Exception> logError, string? npcId = null)
    {
        assetRoot = Path.Combine(modDirectory, "assets", "share"); exportRoot = Path.Combine(modDirectory, "shares");
        this.npcName = npcName; this.playerName = playerName; this.npcId = npcId ?? npcName;
        this.turns = turns; this.returnToChat = returnToChat; this.logError = logError;
        assets = ShareCardModel.LoadCatalog(assetRoot).OrderByDescending(a => a.People?.Contains(this.npcId, StringComparer.OrdinalIgnoreCase) == true).ToArray();
        byId = assets.ToDictionary(a => a.Id); selected = new bool[turns.Count]; if (selected.Length > 0) selected[^1] = true;
        try { npcAvatar = Game1.content.Load<Texture2D>("Portraits\\" + this.npcId); }
        catch { status = T("未找到角色肖像，使用文字头像", "Portrait unavailable; using initials"); }
        title.Text = T(template.TitleZh, template.TitleEn); subtitle.Text = T(template.SubtitleZh, template.SubtitleEn);
        title.BeforeEdit = () => { TextCheckpoint(); titleCustom = true; };
        subtitle.BeforeEdit = () => { TextCheckpoint(); subtitleCustom = true; };
        excerpt.BeforeEdit = () => { TextCheckpoint(); quoteEdited = true; };
        lastTitle = title.Text; lastSubtitle = subtitle.Text;
        EnsureQuote(); Reflow();
    }
    private IEnumerable<string> QuoteOptions()
    {
        foreach (var pair in turns.Select((t, i) => (t, i)).Where(p => selected[p.i]))
        {
            string text = pair.t.Reply;
            yield return text;
            foreach (var sentence in System.Text.RegularExpressions.Regex.Matches(text, @"[^。！？!?\r\n]+[。！？!?]?").Cast<System.Text.RegularExpressions.Match>())
                if (!string.IsNullOrWhiteSpace(sentence.Value)) yield return sentence.Value.Trim();
        }
    }
    private void EnsureQuote()
    {
        if (quoteEdited) return;
        var choices = QuoteOptions().Distinct().ToArray();
        if (!choices.Contains(quote)) quote = choices.Where(s => s.Length <= 54).LastOrDefault() ?? choices.LastOrDefault() ?? "";
        excerpt.Text = quote; lastExcerpt = quote;
    }
    private void Reflow()
    {
        EnsureQuote();
        try
        {
            pages = ShareStudioLayout.Build(turns, selected, template, ratio, title.Text, subtitle.Text, quote, coverEnabled, cardText.Measure);
            layoutError = "";
        }
        catch (InvalidOperationException ex) { layoutError = ex.Message; }
        if (pages.Count == 0) pages.Add(new ShareStudioPage());
        page = Math.Clamp(page, 0, pages.Count - 1); selectedSticker = -1; dirty = true;
        foreach (var p in pagePreviews.Values) p.Dispose(); pagePreviews.Clear();
    }
    private Snapshot Capture() => new(template.Id, ratio.Id, title.Text, subtitle.Text, titleCustom, subtitleCustom, background?.Id,
        backgroundX, backgroundY, selected.ToArray(), stickers.ToArray(), hideName, coverEnabled, quote, quoteEdited, portraitPath, page);
    private void Checkpoint()
    {
        undo.Push(Capture()); redo.Clear();
        if (undo.Count > 40) { var keep = undo.Take(40).Reverse().ToArray(); undo.Clear(); foreach (var s in keep) undo.Push(s); }
    }
    private void TextCheckpoint()
    { if ((DateTime.UtcNow - lastTextCheckpoint).TotalSeconds > .7) Checkpoint(); lastTextCheckpoint = DateTime.UtcNow; }
    private void Restore(Snapshot s)
    {
        template = ShareTemplate.Get(s.Template); ratio = ShareRatio.Get(s.Ratio);
        title.Text = s.Title; subtitle.Text = s.Subtitle; titleCustom = s.TitleCustom; subtitleCustom = s.SubtitleCustom;
        lastTitle = title.Text; lastSubtitle = subtitle.Text;
        background = s.Background is not null && byId.TryGetValue(s.Background, out var bg) ? bg : null;
        backgroundX = s.BackgroundX; backgroundY = s.BackgroundY;
        s.Selected.CopyTo(selected, 0); stickers = s.Stickers.ToList(); hideName = s.HideName; coverEnabled = s.Cover;
        quote = s.Quote; quoteEdited = s.QuoteEdited; excerpt.Text = lastExcerpt = quote;
        portraitPath = s.Portrait; customAvatar = portraitPath is not null ? LoadCustomPortrait(portraitPath) : null;
        page = s.Page; Reflow();
    }
    private void History(bool forward)
    { var from = forward ? redo : undo; var to = forward ? undo : redo; if (from.Count == 0) return; to.Push(Capture()); Restore(from.Pop()); }
    private void ChangeTemplate(string id)
    {
        if (template.Id == id) return; Checkpoint(); template = ShareTemplate.Get(id);
        if (!titleCustom) title.Text = T(template.TitleZh, template.TitleEn);
        if (!subtitleCustom) subtitle.Text = T(template.SubtitleZh, template.SubtitleEn);
        lastTitle = title.Text; lastSubtitle = subtitle.Text; page = 0; Reflow();
    }
    private void ChangeRatio(string id)
    {
        if (ratio.Id == id) return; Checkpoint(); float oldHeight = CardHeight; ratio = ShareRatio.Get(id);
        stickers = stickers.Select(s => s with { Y = Math.Clamp(s.Y / oldHeight * CardHeight, s.Size / 2, CardHeight - 60 - s.Size / 2) }).ToList();
        Reflow();
    }
    private Texture2D? LoadCustomPortrait(string path)
    {
        if (customPortraits.TryGetValue(path, out var cached)) return cached;
        try
        {
            if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new InvalidDataException(T("头像文件需小于 12 MB", "Portrait must be smaller than 12 MB"));
            using var bitmap = SkiaSharp.SKBitmap.Decode(path);
            if (bitmap is null || bitmap.Width > 8192 || bitmap.Height > 8192) throw new InvalidDataException(T("无法读取此头像", "Cannot read this portrait"));
            using var resized = new SkiaSharp.SKBitmap(512, 512, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using (var c = new SkiaSharp.SKCanvas(resized))
            {
                c.Clear(SkiaSharp.SKColors.Transparent); float fit = Math.Min(512f / bitmap.Width, 512f / bitmap.Height);
                c.DrawBitmap(bitmap, new SkiaSharp.SKRect((512-bitmap.Width*fit)/2, (512-bitmap.Height*fit)/2, (512+bitmap.Width*fit)/2, (512+bitmap.Height*fit)/2));
            }
            byte[] pixels = new byte[512*512*4]; System.Runtime.InteropServices.Marshal.Copy(resized.GetPixels(), pixels, 0, pixels.Length);
            var tex = new Texture2D(Game1.graphics.GraphicsDevice, 512, 512); tex.SetData(pixels);
            // History stores paths, so evicted portraits can be reloaded on undo.
            if (customPortraits.Count >= 8)
            {
                string oldest = customPortraits.Keys.First(k => k != portraitPath);
                customPortraits[oldest].Dispose(); customPortraits.Remove(oldest);
            }
            customPortraits[path] = tex; return tex;
        }
        catch (Exception ex) { logError(ex); status = ex.Message; return null; }
    }
    private void ChoosePortrait()
    {
        if (pickingPortrait) return;
        if (!OperatingSystem.IsWindows()) { status = T("此文件选择器目前支持 Windows", "Image picker currently supports Windows"); return; }
        pickingPortrait = true;
        ShareImagePicker.Choose(path => { if (path is not null) pickedPortraits.Enqueue(path); pickingPortrait = false; });
    }
    public override void update(GameTime time)
    {
        base.update(time); if (disposed) return;
        while (pickedPortraits.TryDequeue(out var path))
        { var image = LoadCustomPortrait(path); if (image is not null) { Checkpoint(); portraitPath = path; customAvatar = image; Invalidate(); } }
        if (lastSearch != search.Text) { lastSearch = search.Text; assetPage = 0; }
        if (lastTitle != title.Text || lastSubtitle != subtitle.Text) { lastTitle = title.Text; lastSubtitle = subtitle.Text; Reflow(); }
        if (lastExcerpt != excerpt.Text) { lastExcerpt = excerpt.Text; quote = excerpt.Text.Trim(); Reflow(); }
        try
        {
            if (dirty)
            {
                var next = Render(page, ShareChatLayout.ExportScale); preview?.Dispose(); preview = next; dirty = false;
            }
            int thumb = Enumerable.Range(Math.Max(0, page - 3), Math.Min(pages.Count, page + 4) - Math.Max(0, page - 3))
                .FirstOrDefault(i => !pagePreviews.ContainsKey(i), -1);
            if (thumb >= 0)
            {
                if (pagePreviews.Count >= 8) { var key = pagePreviews.Keys.OrderByDescending(i => Math.Abs(i - page)).First(); pagePreviews[key].Dispose(); pagePreviews.Remove(key); }
                pagePreviews[thumb] = Render(thumb, .18f);
            }
            if (exportRequested) { exportRequested = false; Export(); }
        }
        catch (Exception ex) { dirty = false; exportRequested = false; logError(ex); status = T("生成失败，请查看日志", "Generation failed; see log"); }
    }
    private void Invalidate()
    { dirty = true; foreach (var p in pagePreviews.Values) p.Dispose(); pagePreviews.Clear(); }
    private void Export()
    {
        if (!selected.Any(s => s)) { status = T("请至少选择一轮对话", "Select at least one turn"); return; }
        if (layoutError.Length > 0) { status = layoutError; return; }
        string directory = Path.Combine(exportRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                using var image = Render(i, ShareChatLayout.ExportScale);
                using var file = new FileStream(Path.Combine(directory, $"share-{i + 1:00}.png"), FileMode.CreateNew);
                image.SaveAsPng(file, image.Width, image.Height);
            }
            lastExport = directory; status = T($"已保存 {pages.Count} 张图片", $"Saved {pages.Count} images");
        }
        catch { status = T("导出未完成，请重试", "Export incomplete; retry"); throw; }
    }
    private void ChangeSticker(Func<ShareSticker, ShareSticker> change)
    {
        if (selectedSticker < 0 || selectedSticker >= stickers.Count) return;
        Checkpoint(); stickers[selectedSticker] = change(stickers[selectedSticker]); Invalidate();
    }
    private void RemoveSticker() { if (selectedSticker < 0) return; Checkpoint(); stickers.RemoveAt(selectedSticker); selectedSticker = -1; Invalidate(); }
    private void Layer(int direction)
    {
        if (selectedSticker < 0) return; int next = Math.Clamp(selectedSticker + direction, 0, stickers.Count - 1);
        Checkpoint(); (stickers[next], stickers[selectedSticker]) = (stickers[selectedSticker], stickers[next]); selectedSticker = next; Invalidate();
    }
    private void OpenFolder()
    { try { Directory.CreateDirectory(exportRoot); Process.Start(new ProcessStartInfo(lastExport ?? exportRoot) { UseShellExecute = true }); } catch (Exception ex) { logError(ex); status = T("无法打开文件夹", "Cannot open folder"); } }
    private void Leave() { exitThisMenuNoSound(); returnToChat(); }
    protected override void cleanupBeforeExit() { Dispose(); base.cleanupBeforeExit(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (Inputs.Any(t => ReferenceEquals(Game1.keyboardDispatcher.Subscriber, t))) Game1.keyboardDispatcher.Subscriber = null;
        preview?.Dispose(); cardText.Dispose(); uiText.Dispose();
        foreach (var tex in textures.Values.Concat(customPortraits.Values).Concat(templatePreviews.Values).Concat(pagePreviews.Values).Distinct()) tex.Dispose();
        textures.Clear(); customPortraits.Clear(); pagePreviews.Clear(); templatePreviews.Clear();
    }
    private IEnumerable<ShareStudioInput> Inputs => new[] { title, subtitle, excerpt, search };
    private Texture2D? Texture(string path)
    {
        if (textures.TryGetValue(path, out var texture)) return texture;
        if (failedTextures.Contains(path)) return null;
        try
        {
            using var stream = File.OpenRead(ShareCardModel.SafePath(assetRoot, path));
            texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
            // Texture2D.FromStream supplies straight alpha; SpriteBatch uses premultiplied alpha.
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i]; pixels[i] = new Color(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255, c.A);
            }
            texture.SetData(pixels);
            long bytes = (long)texture.Width * texture.Height * 4;
            while (textures.Count > 0 && (textures.Count >= 40 || textureBytes + bytes > 64 * 1024 * 1024))
            {
                string oldest = textureOrder.Dequeue();
                textureBytes -= (long)textures[oldest].Width * textures[oldest].Height * 4;
                textures[oldest].Dispose(); textures.Remove(oldest);
            }
            textureBytes += bytes;
            textures[path] = texture; textureOrder.Enqueue(path);
            return texture;
        }
        catch (Exception ex) { failedTextures.Add(path); logError(ex); status = T("部分素材无法读取", "Some assets could not be loaded"); return null; }
    }


    private void DrawAvatar(SpriteBatch batch, Texture2D? portrait, Rectangle bounds, bool player)
    {
        batch.Draw(Game1.staminaRect, bounds, new Color(221, 221, 216));
        if (player && !playerAvatarFailed && Game1.player is Farmer farmer
            && farmer.FarmerRenderer is FarmerRenderer renderer)
        {
            bool previous = FarmerRenderer.isDrawingForUI;
            FarmerRenderer.isDrawingForUI = true;
            try
            {
                // Borrow the live farmer's initialized renderer so texture names,
                // skin and eye colors match the game. The game owns its lifetime.
                renderer.drawMiniPortrat(batch, new Vector2(bounds.X, bounds.Y), .96f,
                    bounds.Width / 16f, 2, farmer);
                return;
            }
            catch (Exception ex)
            {
                // Disable only this menu's portrait attempt; leave the shared
                // renderer untouched and keep preview/export usable.
                playerAvatarFailed = true;
                status = T("玩家头像暂不可用，已使用文字头像", "Player portrait unavailable; using an initial");
                logError(new InvalidOperationException(
                    "Player portrait unavailable; using a text avatar for this share editor session.", ex));
                // Cover any partially drawn portrait before drawing the fallback.
                batch.Draw(Game1.staminaRect, bounds, new Color(221, 221, 216));
            }
            finally { FarmerRenderer.isDrawingForUI = previous; }
        }

        if (portrait is null)
        {
            string fallback = player ? T("我", "Me") : npcName[..Math.Min(1, npcName.Length)];
            float size = Math.Min(32, (bounds.Width - 12) / Math.Max(1, cardText.Measure(fallback, 1)));
            cardText.Draw(batch, fallback,
                new Vector2(bounds.Center.X - cardText.Measure(fallback, size) / 2, bounds.Center.Y - size * .7f),
                size, new Color(80, 80, 76), textOutputScale);
            return;
        }

        Rectangle source = !ReferenceEquals(portrait, customAvatar) && portrait.Width >= 64 && portrait.Height >= 64
            ? new Rectangle(0, 0, 64, 64)
            : portrait.Bounds;
        batch.Draw(portrait, bounds, source, Color.White);
    }

    private void DrawFramedAvatar(SpriteBatch batch, Texture2D? portrait, Rectangle bounds, bool player)
    {
        DrawRoundedRect(batch, new Rectangle(bounds.X - 3, bounds.Y + 1, bounds.Width + 6, bounds.Height + 6), 8, Color.Black * .15f);
        DrawRoundedRect(batch, new Rectangle(bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6), 8, new Color(255, 255, 252));
        DrawAvatar(batch, portrait, bounds, player);
    }

    private static void DrawRoundedRect(SpriteBatch batch, Rectangle bounds, int radius, Color color)
    {
        radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y + radius, bounds.Width, bounds.Height - radius * 2), color);
        for (int y = 0; y < radius; y++)
        {
            float dy = radius - y - .5f;
            int inset = (int)MathF.Ceiling(radius - MathF.Sqrt(radius * radius - dy * dy));
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.X + inset, bounds.Y + y, bounds.Width - inset * 2, 1), color);
            batch.Draw(Game1.staminaRect, new Rectangle(bounds.X + inset, bounds.Bottom - y - 1, bounds.Width - inset * 2, 1), color);
        }
    }


}

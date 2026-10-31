using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using VivantValley;
using VivantValley.Menus;
using VivantValley.Services;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

using var probe = new Probe(Path.GetFullPath(args[0]));
probe.RunOneFrame();

sealed class Probe : Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly string root;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int cmd);
    public Probe(string root)
    {
        this.root = root;
        graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 800 };
        Window.Position = new Point(-20000, -20000);
        ShowWindow(Window.Handle, 0);
        Content.RootDirectory = @"E:\SteamLibrary\steamapps\common\Stardew Valley\Content";
    }
    protected override void LoadContent()
    {
        ShowWindow(Window.Handle, 0);
        Game1.graphics = graphics;
        Game1.game1 = (Game1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GameRunner.instance = (GameRunner)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GameRunner));
        GameRunner.instance.gameInstances = new List<Game1> { Game1.game1 };
        Game1.keyboardDispatcher = new KeyboardDispatcher(Window);
        Game1.content = new LocalizedContentManager(Services, Content.RootDirectory);
        Game1.staminaRect = new Texture2D(GraphicsDevice, 1, 1);
        Game1.staminaRect.SetData(new[] { Color.White });
        Game1.uiViewport = new xTile.Dimensions.Rectangle(0, 0, 1280, 800);
        Game1.mouseCursors = Content.Load<Texture2D>("LooseSprites/Cursors");
        Game1.options = (Options)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Options));
        foreach (var field in typeof(Options).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(float) && (field.Name.Contains("zoom", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("scale", StringComparison.OrdinalIgnoreCase))) field.SetValue(Game1.options, 1f);
        Game1.input = new InputState();
        Game1.currentGameTime = new GameTime();
        Game1.game1._locations = new List<GameLocation>();
        Game1._locationLookup = new Dictionary<string, GameLocation>();
        CraftingRecipe.craftingRecipes = DataLoader.CraftingRecipes(Game1.content);
        CraftingRecipe.cookingRecipes = DataLoader.CookingRecipes(Game1.content);
        Game1.shirtData = DataLoader.Shirts(Game1.content);
        FarmerRenderer.hairStylesTexture = Content.Load<Texture2D>("Characters/Farmer/hairstyles");
        FarmerRenderer.shirtsTexture = Content.Load<Texture2D>("Characters/Farmer/shirts");
        typeof(Game1).GetProperty("player")!.SetValue(null, new Farmer());
        Game1.player.changeHairStyle(0);
        Game1.player.changeHairColor(new Color(112, 65, 31));
        VerifyStudio();
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object obj, string method, params object?[] args)
        => obj.GetType().GetMethod(method, Flags)!.Invoke(obj, args);
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Flags)!.GetValue(obj)!;
    private static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Flags)!.SetValue(obj, value);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
    private void Save(RenderTarget2D target, string path)
    { using var file = File.Create(path); target.SaveAsPng(file, target.Width, target.Height); }
    private void VerifyStudio()
    {
        string output = Path.Combine(root, "reviews", "share-studio-validation"); Directory.CreateDirectory(output);
        int images = 0;
        foreach (string locale in new[] { "zh", "en" })
        using (L.BeginRequest(locale))
        {
            Game1.smallFont = Content.Load<SpriteFont>(locale == "zh" ? "Fonts/SmallFont.zh-CN" : "Fonts/SmallFont");
            var turns = new[] { new ShareTurn(locale == "zh" ? "你是不是……只对我这样？" : "Is this side of you just for me?",
                locale == "zh" ? "别盯着我看。我会忍不住，想离你再近一点。" : "Don't look at me like that. I might want to stay a little closer.") };
            foreach (var t in ShareTemplate.All)
            {
                string id = t.Id == "06" ? "Shane" : t.Id == "07" ? "Haley" : "Sebastian";
                string name = locale == "zh" ? id == "Shane" ? "谢恩" : id == "Haley" ? "海莉" : "塞巴斯蒂安" : id;
                var errors = new List<Exception>();
                using var menu = new ShareCardMenu(root, name, "Farmer", turns, () => { }, errors.Add, id);
                Call(menu, "ChangeTemplate", t.Id);
                foreach (var ratio in ShareRatio.All)
                {
                    Call(menu, "ChangeRatio", ratio.Id);
                    Check(Get<string>(menu, "layoutError").Length == 0, $"{locale}/{t.Id}/{ratio.Id} layout error: " + Get<string>(menu, "layoutError"));
                    var pages = Get<List<ShareStudioPage>>(menu, "pages");
                    string text = string.Concat(pages.Where(p => !p.Cover).SelectMany(p => p.Messages).SelectMany(m => m.Bubble.Lines));
                    Check(Compact(text) == Compact(turns[0].Player + turns[0].Reply), "Pagination dropped text");
                    AssertFits(pages, ratio.Height);
                    for (int pi = 0; pi < pages.Count; pi++)
                    {
                        using var target = (RenderTarget2D)Call(menu, "Render", pi, 2f)!;
                        Check(target.Width == 1440 && target.Height == ratio.Height * 2, "Wrong export dimensions");
                        Save(target, Path.Combine(output, $"{locale}-{t.Id}-{ratio.Id.Replace(':','x')}-{pi+1}.png")); images++;
                    }
                    Check(errors.Count == 0, "Render errors: " + errors.FirstOrDefault());
                }
                if (t.Id == "05")
                {
                    Call(menu, "ChangeRatio", "3:4"); menu.update(new GameTime());
                    foreach (var size in new[] { (1280, 800), (1920, 1080), (1024, 768), (960, 600), (800, 600) })
                    {
                        foreach (string tab in new[] { "templates", "text", "background", "stickers", "turns" })
                        {
                            Call(menu, "SetTab", tab); DrawEditor(menu, output, $"{locale}-editor-{size.Item1}-{size.Item2}-{tab}.png", size.Item1, size.Item2);
                            var buttons = Get<List<(Rectangle Bounds, string Label, Action Click)>>(menu, "buttons");
                            Check(buttons.All(b => b.Bounds.X >= 0 && b.Bounds.Y >= 0 && b.Bounds.Right <= size.Item1 && b.Bounds.Bottom <= size.Item2), "Editor control outside viewport");
                        }
                    }
                    VerifyEditing(menu, output, locale);
                }
            }
            VerifyLongLayouts(locale);
            VerifyOutlineText(output, locale);
            VerifyPortraitsAndBackground(output, locale, turns);
        }
        Console.WriteLine($"Share studio passed: {images} exports, 7 templates × 4 ratios × 2 locales, all editor tabs at 5 viewport sizes, history, portraits and full-color backgrounds.");
    }
    private static void AssertFits(IEnumerable<ShareStudioPage> pages, int height)
    {
        foreach (var p in pages)
        {
            Check(p.BodyTop >= 100, "Invalid body origin");
            foreach (var m in p.Messages)
            {
                Check(m.Bounds.X >= 0 && m.Bounds.X + m.Bounds.Width <= 720.5f, "Message outside horizontal canvas");
                Check(m.Bounds.Y + m.Bounds.Height <= height - 87, "Message overlaps footer");
                Check(m.Bubble.Lines.Count > 0, "Empty message chunk");
            }
        }
    }
    private void VerifyLongLayouts(string locale)
    {
        using var font = new ShareCardTextRenderer();
        string paragraph = string.Concat(Enumerable.Repeat(locale == "zh" ? "你好，星露谷。👩‍🌾一起散步吧！" : "Hello, Pelican Town. 👩‍🌾 Let's walk together! ", 35));
        var turns = Enumerable.Range(0, 5).Select(i => new ShareTurn(paragraph + i, paragraph + i)).ToArray();
        foreach (var t in ShareTemplate.All)
            foreach (var r in ShareRatio.All)
            {
                var pages = ShareStudioLayout.Build(turns, new[] { true, false, true, false, true }, t, r,
                    locale == "zh" ? t.TitleZh : t.TitleEn, "", "", false, font.Measure);
                AssertFits(pages, r.Height);
                string expected = string.Concat(new[] { 0, 2, 4 }.SelectMany(i => new[] { turns[i].Player, turns[i].Reply }));
                string actual = string.Concat(pages.SelectMany(p => p.Messages).SelectMany(m => m.Bubble.Lines));
                Check(Compact(expected) == Compact(actual), "Long selected conversation lost or reordered text");
                Check(pages.Count > 1, "Long conversation not paginated");
            }
        bool overflow = false;
        try { ShareStudioLayout.Build(turns, new[] { true, false, false, false, false }, ShareTemplate.All[0], ShareRatio.All[0], paragraph, "", "", false, font.Measure); }
        catch (InvalidOperationException) { overflow = true; }
        Check(overflow, "Oversized title wasn't rejected");
        Console.WriteLine($"Verified {locale}: 28 long-content layouts preserve selected Unicode messages and paginate safely.");
    }
    private void DrawEditor(ShareCardMenu menu, string output, string file, int width, int height)
    {
        Game1.uiViewport = new xTile.Dimensions.Rectangle(0, 0, width, height);
        using var target = new RenderTarget2D(GraphicsDevice, width, height);
        GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
        using var b = new SpriteBatch(GraphicsDevice); b.Begin(); menu.draw(b); b.End(); GraphicsDevice.SetRenderTarget(null);
        Save(target, Path.Combine(output, file));
    }
    private void VerifyEditing(ShareCardMenu menu, string output, string locale)
    {
        var title = (IKeyboardSubscriber)Get<object>(menu, "title");
        Call(menu, "SetTab", "text"); DrawEditor(menu, output, locale + "-before-edit.png", 1280, 800);
        var bounds = (Rectangle)title.GetType().GetField("Bounds")!.GetValue(title)!;
        menu.receiveLeftClick(bounds.X + 12, bounds.Y + 12);
        title.GetType().GetProperty("Text")!.SetValue(title, "");
        title.RecieveTextInput(locale == "zh" ? "我们的小秘密" : "Our little secret"); title.RecieveCommandInput('\r'); title.RecieveTextInput("♥");
        menu.update(new GameTime());
        string typed = (string)title.GetType().GetProperty("Text")!.GetValue(title)!;
        Check(typed.Contains('\n'), "Title newline was lost");
        Call(menu, "ChangeTemplate", "07");
        Check((string)title.GetType().GetProperty("Text")!.GetValue(title)! == typed, "Switching template lost custom title");
        Call(menu, "History", false); Check(Get<ShareTemplate>(menu, "template").Id == "05", "Template undo failed");
        Call(menu, "History", true); Check(Get<ShareTemplate>(menu, "template").Id == "07", "Template redo failed");
        var catalog = ShareCardModel.LoadCatalog(Path.Combine(root, "assets/share"));
        Call(menu, "Checkpoint"); var beforeBg = Get<ShareAsset?>(menu, "background");
        Set(menu, "background", catalog.First(a => a.Kind == "background")); Call(menu, "History", false);
        Check(Get<ShareAsset?>(menu, "background")?.Id == beforeBg?.Id, "Background undo failed");
        var savedTitle = (string)title.GetType().GetProperty("Text")!.GetValue(title)!;
        Call(menu, "ChangeTemplate", "04"); title.GetType().GetProperty("Text")!.SetValue(title, ""); menu.update(new GameTime());
        var excerpt = (IKeyboardSubscriber)Get<object>(menu, "excerpt");
        excerpt.Selected = true;
        excerpt.GetType().GetProperty("Text")!.SetValue(excerpt, "");
        string original = Get<IReadOnlyList<ShareTurn>>(menu, "turns")[0].Reply;
        string fragment = locale == "zh" ? "别盯着我看" : "Don't look at me";
        Check(original.Contains(fragment), "Test fragment invalid");
        excerpt.RecieveTextInput(fragment); menu.update(new GameTime());
        Check(Get<string>(menu,"layoutError").Length == 0 && Get<string>(menu,"quote") == fragment, "Original excerpt rejected");
        excerpt.RecieveTextInput(" not an original quote"); menu.update(new GameTime());
        Check(Get<string>(menu,"layoutError").Length > 0,"Invented quote accepted");
        Set(menu,"quoteEdited",false); Set(menu,"quote",fragment); Call(menu,"ChangeTemplate","07");
        title.GetType().GetProperty("Text")!.SetValue(title,savedTitle); menu.update(new GameTime());
        Call(menu,"SetTab","templates"); DrawEditor(menu,output,locale+"-scrolled-templates.png",1280,800);
        var track = Get<Rectangle>(menu,"scrollTrack");
        menu.receiveLeftClick(track.Center.X,track.Bottom-2); menu.releaseLeftClick(track.Center.X,track.Bottom-2);
        Check(Get<int>(menu,"panelScroll") > 0,"Scrollbar drag did not move panel");
        DrawEditor(menu,output,locale+"-scrolled-templates.png",1280,800);
        Check(Get<List<(Rectangle Bounds,string Label,Action Click)>>(menu,"buttons").Any(b=>b.Label== (locale=="zh"?"脸红日记":"The blush diary")), "Last template not reachable");
        var sticker = catalog.First(a => a.Kind == "sticker" && a.Category == "animals");
        var stickers = Get<List<ShareSticker>>(menu, "stickers"); stickers.Add(new(sticker.Id, 0, 360, 690, 110)); Set(menu, "selectedSticker", 0);
        Call(menu, "ChangeSticker", (Func<ShareSticker,ShareSticker>)(s => s with { Flipped = true }));
        Call(menu, "History", false); Check(!Get<List<ShareSticker>>(menu, "stickers")[0].Flipped, "Sticker undo failed");
        Call(menu, "History", true); Check(Get<List<ShareSticker>>(menu, "stickers")[0].Flipped, "Sticker redo failed");
        Call(menu, "ChangeRatio", "1:1"); Check(Get<List<ShareSticker>>(menu, "stickers")[0].Y < 660, "Ratio change left sticker off canvas");
        Call(menu, "History", false); Check(Get<ShareRatio>(menu, "ratio").Id == "3:4", "Ratio undo failed");
        string path = Path.Combine(root, "assets/share", catalog.First(a => a.Kind == "sticker" && a.Name == "Haley").Path);
        var tex = (Texture2D)Call(menu, "LoadCustomPortrait", path)!; Check(tex.Width == 512, "Custom portrait decode failed");
        Set(menu, "customAvatar", tex); Set(menu, "portraitPath", path); Call(menu, "Reflow"); menu.update(new GameTime());
        using (var image = (RenderTarget2D)Call(menu, "Render", 0, 2f)!) Save(image, Path.Combine(output, locale + "-custom-portrait.png"));
        Set(menu, "exportRoot", Path.Combine(output, "exports", locale)); Call(menu, "Export");
        Check(Directory.EnumerateFiles(Path.Combine(output, "exports", locale), "*.png", SearchOption.AllDirectories).Any(), "Export did not create PNG");
        title.GetType().GetProperty("Text")!.SetValue(title, new string('长', 150)); menu.update(new GameTime());
        Check(Get<string>(menu, "layoutError").Length > 0, "Overflow not reported");
        title.GetType().GetProperty("Text")!.SetValue(title, ""); menu.update(new GameTime());
        Check(Get<string>(menu, "layoutError").Length == 0 && Get<List<ShareStudioPage>>(menu, "pages")[0].TitleLines.Length == 0, "Empty title not hidden");
        Console.WriteLine($"Verified {locale}: multiline title editing, template/ratio/sticker history, custom portrait and PNG export.");
    }
    private void VerifyPortraitsAndBackground(string output, string locale, ShareTurn[] turns)
    {
        var errors = new List<Exception>(); using var menu = new ShareCardMenu(root, "Haley", "Farmer", turns, () => {}, errors.Add, "Haley");
        foreach (bool male in new[] { true, false })
        {
            Game1.player.changeGender(male); Game1.player.changeHairStyle(male ? 0 : 16);
            using var actual = RenderAvatar(menu); using var expected = RenderAvatar(null);
            var ap = new Color[actual.Width * actual.Height]; var ep = new Color[ap.Length]; actual.GetData(ap); expected.GetData(ep);
            Check(ap.SequenceEqual(ep), "Live player avatar mismatch");
        }
        var catalog = ShareCardModel.LoadCatalog(Path.Combine(root, "assets/share")); var bg = catalog.First(a => a.Kind == "background" && a.Category == "Pattern");
        Set(menu, "background", bg); using var target = (RenderTarget2D)Call(menu, "Render", 0, 1f)!;
        var tex = (Texture2D)Call(menu, "Texture", bg.Path)!;
        using var expectedBg = new RenderTarget2D(GraphicsDevice, 720, 960); GraphicsDevice.SetRenderTarget(expectedBg);
        using (var b = new SpriteBatch(GraphicsDevice)) { b.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp); float fit = Math.Max(720f/tex.Width,960f/tex.Height); b.Draw(tex,new Vector2((720-tex.Width*fit)/2,(960-tex.Height*fit)/2),null,Color.White,0,Vector2.Zero,fit,SpriteEffects.None,0); b.End(); }
        GraphicsDevice.SetRenderTarget(null);
        var region = new Rectangle(0,100,12,770);var a = new Color[region.Width*region.Height];var e = new Color[a.Length];target.GetData(0,region,a,0,a.Length);expectedBg.GetData(0,region,e,0,e.Length);
        Check(a.SequenceEqual(e),"Background faded or covered");
        var renderer = Game1.player.FarmerRenderer;var broken = new FarmerRenderer();Game1.player.FarmerRenderer=broken;
        try { for(int i=0;i<3;i++){using var result=(RenderTarget2D)Call(menu,"Render",0,1f)!;} Check(errors.Count==1,"Avatar fallback should log once"); }
        finally{Game1.player.FarmerRenderer=renderer;broken.unload();}
        Console.WriteLine($"Verified {locale}: live male/female portraits, single fallback diagnostic and full-color wallpaper.");
    }
    private void VerifyOutlineText(string output, string locale)
    {
        using var text = new ShareCardTextRenderer();
        string sample = locale == "zh" ? "一起去玛尼家吧。" : "Let's visit Marnie.";
        RenderTarget2D TextImage(float scale)
        {
            var target = new RenderTarget2D(GraphicsDevice, (int)(400 * scale), (int)(64 * scale));
            GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Transparent);
            using var batch = new SpriteBatch(GraphicsDevice);
            batch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, Matrix.CreateScale(scale));
            text.Draw(batch, sample, new Vector2(5, 2), ShareChatLayout.BodyFontSize, Color.Black, scale);
            batch.End(); GraphicsDevice.SetRenderTarget(null);
            return target;
        }
        using var low = TextImage(1);
        using var high = TextImage(2);
        var lowPixels = new Color[low.Width * low.Height]; low.GetData(lowPixels);
        var highPixels = new Color[high.Width * high.Height]; high.GetData(highPixels);
        int coverageLevels = highPixels.Where(c => c.A > 0 && c.A < 255).Select(c => c.A).Distinct().Count();
        using (var diagnostic = File.Create(Path.Combine(output, $"{locale}-outline-text.png"))) high.SaveAsPng(diagnostic, high.Width, high.Height);
        if (coverageLevels < 12) throw new Exception($"Text has no smooth grayscale antialiased edges: {coverageLevels} levels");
        int rerasterized = 0;
        for (int y = 0; y < high.Height; y++)
            for (int x = 0; x < high.Width; x++)
                if (highPixels[y * high.Width + x].A != lowPixels[(y / 2) * low.Width + x / 2].A) rerasterized++;
        if (rerasterized < 250) throw new Exception("Export text is an enlarged low-resolution bitmap");
        var lastInkX = Enumerable.Range(0, highPixels.Length).Where(i => highPixels[i].A > 0).Max(i => i % high.Width);
        if (lastInkX > Math.Ceiling((5 + text.Measure(sample, ShareChatLayout.BodyFontSize)) * 2) + 4)
            throw new Exception("Font measurement did not match output glyph width");
        using (var file = File.Create(Path.Combine(output, $"{locale}-outline-text.png"))) high.SaveAsPng(file, high.Width, high.Height);
        var layout = new ShareChatLayout(ShareChatLayout.BodyFontSize * 1.5f);
        string longMessage = string.Concat(Enumerable.Repeat(locale == "zh" ? "你好星露谷。" : "HelloWorld!", 180));
        var pages = ShareCardModel.PaginateBubbles(new[] { new ShareTurn(longMessage, longMessage) }, new[] { true },
            value => text.Measure(value, ShareChatLayout.BodyFontSize), layout);
        if (pages.Count < 2 || string.Concat(pages.SelectMany(p => p).SelectMany(b => b.Lines)) != longMessage + longMessage)
            throw new Exception("Outline-font pagination lost long messages");
        foreach (var page in pages)
        {
            if (page.Sum(b => Math.Max(b.Height, ShareChatLayout.AvatarSize)) + (page.Count - 1) * ShareChatLayout.RowGap > ShareChatLayout.PageHeight)
                throw new Exception("Outline-font pagination exceeds 3:4 page bounds");
            if (page.SelectMany(b => b.Lines).Any(line => text.Measure(line, ShareChatLayout.BodyFontSize) > ShareChatLayout.TextWidth + .01f))
                throw new Exception("Outline-font text exceeds bubble width");
        }
        Console.WriteLine($"Verified {locale} outline text: {text.FontFamily}; {coverageLevels} antialias coverage levels; native 2x raster differs from bitmap enlargement; long messages fit 3:4 pages.");
    }

    private RenderTarget2D RenderAvatar(ShareCardMenu? menu)
    {
        var previous = GraphicsDevice.GetRenderTargets();
        var viewport = GraphicsDevice.Viewport;
        var target = new RenderTarget2D(GraphicsDevice, ShareChatLayout.AvatarSize, ShareChatLayout.AvatarSize);
        bool priorUiState = FarmerRenderer.isDrawingForUI;
        try
        {
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(new Color(221, 221, 216));
            using var batch = new SpriteBatch(GraphicsDevice);
            batch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp);
            if (menu is not null)
                typeof(ShareCardMenu).GetMethod("DrawAvatar", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(menu, new object?[] { batch, null, new Rectangle(0, 0, ShareChatLayout.AvatarSize, ShareChatLayout.AvatarSize), true });
            else
            {
                FarmerRenderer.isDrawingForUI = true;
                Game1.player.FarmerRenderer.drawMiniPortrat(batch, Vector2.Zero, .96f, ShareChatLayout.AvatarSize / 16f, 2, Game1.player);
            }
            batch.End();
            return target;
        }
        catch { target.Dispose(); throw; }
        finally
        {
            // Preserve the menu's resulting flag for the regression assertion.
            if (menu is null) FarmerRenderer.isDrawingForUI = priorUiState;
            GraphicsDevice.SetRenderTargets(previous);
            GraphicsDevice.Viewport = viewport;
        }
    }
}

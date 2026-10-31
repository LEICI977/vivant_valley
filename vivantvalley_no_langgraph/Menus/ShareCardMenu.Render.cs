using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed partial class ShareCardMenu
{
    private static Color Hex(string hex) => new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    private static Rectangle Rect(ShareBox r) => new((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    private RenderTarget2D Render(int pageIndex, float scale)
    {
        var device = Game1.graphics.GraphicsDevice;
        var previous = device.GetRenderTargets(); var viewport = device.Viewport;
        float oldScale = textOutputScale; textOutputScale = scale;
        var target = new RenderTarget2D(device, (int)(CardWidth * scale), (int)(CardHeight * scale), false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        using var b = new SpriteBatch(device);
        try
        {
            device.SetRenderTarget(target); device.Clear(Hex(template.Background));
            b.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, null, Matrix.CreateScale(scale));
            var p = pages[Math.Clamp(pageIndex, 0, pages.Count - 1)];
            DrawWallpaper(b);
            Color ink = Hex(template.Ink), muted = Hex(template.Muted), accent = Hex(template.Accent);
            void Text(string text, float x, float y, float size, Color color, bool bold = false)
                => cardText.Draw(b, text, new Vector2(x, y), size, color, scale, bold);
            void Box(float x, float y, float w, float h, Color color, int radius = 0)
                => DrawRoundedRect(b, new Rectangle((int)x, (int)y, (int)w, (int)h), radius, color);
            // A local identity bar guarantees legibility even with a busy wallpaper.
            Box(28, 20, 664, 65, Hex(template.Background), 10);
            DrawFramedAvatar(b, customAvatar ?? npcAvatar, new Rectangle(43, 28, 48, 48), false);
            Text(cardText.Fit(npcName, 25, 365, true), 110, 31, 25, ink, true);
            string tag = T(template.Chinese, template.English);
            Text(tag, CardWidth - 46 - cardText.Measure(tag, 15), 42, 15, muted);
            if (p.First)
            {
                if (background is not null && (p.TitleLines.Length > 0 || p.SubtitleLines.Length > 0))
                    Box(32, 97, 656, p.BodyTop - 111, Hex(template.Background), 10);
                float y = CardHeight == 720 ? 98 : 106;
                foreach (string line in p.TitleLines) { Text(line, 44, y, p.TitleSize, ink, true); y += p.TitleSize * (CardHeight == 720 ? 1.28f : 1.36f); }
                y = p.SubtitleY;
                foreach (string line in p.SubtitleLines) { Text(line, 47, y, 19, muted); y += 29; }
            }
            if (p.HeroPortrait is ShareBox hero) DrawHero(b, hero);
            if (p.Cover)
            {
                Text("“", p.QuoteBounds.X, p.QuoteBounds.Y - 16, 52, accent, true);
                float y = p.QuoteBounds.Y + 32;
                foreach (string line in p.QuoteLines) { Text(line, p.QuoteBounds.X, y, p.QuoteSize, ink); y += p.QuoteSize * 1.45f; }
            }
            foreach (var message in p.Messages) DrawMessage(b, message);
            float contentEnd = Math.Max(p.HeroPortrait is ShareBox hr ? hr.Y + hr.Height : 0,
                p.Messages.Count > 0 ? p.Messages.Max(m => m.Bounds.Y + m.Bounds.Height) : p.BodyTop);
            if (p.First && CardHeight - contentEnd > 220)
            {
                Box(46, CardHeight - 156, 106, 2, accent * .6f);
                Text("A MOMENT TO KEEP", 46, CardHeight - 140, 13, muted);
                string? decoration = template.Id switch { "02" => "sticker-1a9216106aed469d", "06" => "sticker-4a99c25e98f33b20", "07" => "sticker-ab854f723a2f02a2", _ => null };
                if (decoration is not null && byId.TryGetValue(decoration, out var deco) && Texture(deco.Path) is Texture2D dt)
                    b.Draw(dt, new Rectangle(606, CardHeight - 158, 54, 54), Color.White);
                if (template.Id == "05") DrawHeart(b, 618, CardHeight - 143, 24, accent);
            }
            foreach (var s in stickers.Where(s => s.Page == pageIndex))
            {
                if (!byId.TryGetValue(s.AssetId, out var a) || Texture(a.Path) is not Texture2D tex) continue;
                b.Draw(tex, new Vector2(s.X, s.Y), null, Color.White, s.Rotation, new Vector2(tex.Width, tex.Height) / 2,
                    s.Size / Math.Max(tex.Width, tex.Height), s.Flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0);
            }
            Box(0, CardHeight - 65, CardWidth, 65, Hex(template.Background));
            Box(44, CardHeight - 64, 632, 1, muted * .4f);
            Text("VIVANT VALLEY", 44, CardHeight - 44, 15, muted, true);
            string footer = hideName ? "灵动谷" : playerName;
            footer = cardText.Fit(footer, 15, 280);
            Text(footer, CardWidth - 44 - cardText.Measure(footer, 15), CardHeight - 44, 15, muted);
            b.End(); return target;
        }
        catch { target.Dispose(); throw; }
        finally { textOutputScale = oldScale; device.SetRenderTargets(previous); device.Viewport = viewport; }
    }
    private void DrawWallpaper(SpriteBatch b)
    {
        ShareAsset? bg = background;
        if (bg is null)
        {
            string? id = template.Id switch { "01" => "background-b139afa6de5623d4", "03" => "background-19db83ddd905a4d9", _ => null };
            if (id is not null) byId.TryGetValue(id, out bg);
        }
        if (bg is not null && Texture(bg.Path) is Texture2D tex)
        {
            float fit = Math.Max(CardWidth / (float)tex.Width, CardHeight / (float)tex.Height);
            b.Draw(tex, new Vector2((CardWidth - tex.Width * fit) * backgroundX, (CardHeight - tex.Height * fit) * backgroundY),
                null, Color.White, 0, Vector2.Zero, fit, SpriteEffects.None, 0);
            return;
        }
        if (template.Id is "02" or "06")
        {
            for (int y = 24; y < CardHeight - 65; y += 28)
                b.Draw(Game1.staminaRect, new Rectangle(24, y, 672, 1), Hex("B8A78D") * .17f);
            if (template.Id == "02")
                for (int x = 24; x < 710; x += 28) b.Draw(Game1.staminaRect, new Rectangle(x, 24, 1, CardHeight - 95), Hex("B8A78D") * .12f);
        }
        if (template.Id is "05" or "07")
        {
            var border = Hex(template.Accent) * .35f;
            DrawRoundedRect(b, new Rectangle(21, 15, 678, CardHeight - 30), 16, border);
            DrawRoundedRect(b, new Rectangle(22, 16, 676, CardHeight - 32), 15, Hex(template.Background));
        }
    }
    private void DrawHero(SpriteBatch b, ShareBox h)
    {
        var r = Rect(h); var ink = Hex(template.Ink);
        int radius = template.Id == "05" ? Math.Min(r.Width / 2, r.Height / 2) : 8;
        var mat = template.Id switch { "05" => Hex("D0B6CB"), "06" => Hex("BAC8C6"), "07" => Hex("F0D3C5"), "04" => Hex("DBE4B2"), _ => Hex("DEE6D2") };
        DrawRoundedRect(b, new Rectangle(r.X - 6, r.Y + 5, r.Width + 12, r.Height + 6), 8, Color.Black * .07f);
        DrawRoundedRect(b, r, radius, mat);
        int size = Math.Min(r.Width - 24, r.Height - 48);
        var portrait = new Rectangle(r.Center.X - size / 2, r.Y + 7, size, size);
        // Portrait is an independent layer, including content-pack replacements.
        var tex = customAvatar ?? npcAvatar;
        if (tex is not null)
        {
            var source = ReferenceEquals(tex, customAvatar) ? tex.Bounds : tex.Width >= 64 && tex.Height >= 64 ? new Rectangle(0, 0, 64, 64) : tex.Bounds;
            b.Draw(tex, portrait, source, Color.White);
        }
        else DrawAvatar(b, null, portrait, false);
        var label = new Rectangle(r.X + 8, r.Bottom - 39, r.Width - 16, 32);
        DrawRoundedRect(b, label, 5, new Color(255, 250, 243));
        string name = cardText.Fit(npcName, 21, label.Width - 14, true);
        cardText.Draw(b, name, new Vector2(label.Center.X - cardText.Measure(name, 21, true) / 2, label.Y + 2), 21, Hex("54424D"), textOutputScale, true);
        if (template.Id == "06")
            DrawRoundedRect(b, new Rectangle(r.Center.X - 48, r.Y - 9, 96, 18), 1, Hex("C1B89D"));
        if (template.Id is "05" or "07") DrawHeart(b, r.Right - 15, r.Y - 10, 31, Hex(template.Accent));
        if (template.Id == "07")
        {
            DrawRoundedRect(b, new Rectangle(r.Center.X - 38, r.Y - 13, 34, 16), 8, Hex("DEA8B3"));
            DrawRoundedRect(b, new Rectangle(r.Center.X + 4, r.Y - 13, 34, 16), 8, Hex("DEA8B3"));
        }
    }
    private static void DrawHeart(SpriteBatch b, int x, int y, int size, Color color)
    {
        // Small geometric heart; it scales independently of portraits and text.
        DrawRoundedRect(b, new Rectangle(x, y, size / 2 + 1, size / 2), size / 4, color);
        DrawRoundedRect(b, new Rectangle(x + size / 2 - 1, y, size / 2 + 1, size / 2), size / 4, color);
        for (int row = 0; row < size / 2; row++)
            b.Draw(Game1.staminaRect, new Rectangle(x + row, y + size / 3 + row, size - row * 2, 1), color);
    }
    private void DrawMessage(SpriteBatch b, SharePlacedMessage placed)
    {
        var message = placed.Bubble; var r = Rect(placed.Bounds);
        bool chat = template.Chat && !placed.Hero;
        var surface = Hex(message.IsPlayer ? template.PlayerPaper : template.Paper);
        Color ink = !message.IsPlayer && template.Id == "06" ? Hex("FFF4E1")
            : message.IsPlayer && template.Id is "03" or "05" ? Hex("FAECF1") : Hex("304238");
        DrawRoundedRect(b, new Rectangle(r.X, r.Y + 4, r.Width, r.Height), 12, Color.Black * .07f);
        DrawRoundedRect(b, r, template.Id is "02" or "06" ? 4 : 14, surface);
        float x = r.X + 22, y = r.Y + 15;
        if (chat)
        {
            int avatarX = message.IsPlayer ? 601 : 43;
            DrawFramedAvatar(b, message.IsPlayer ? null : customAvatar ?? npcAvatar, new Rectangle(avatarX, r.Y + 3, 76, 76), message.IsPlayer);
            for (int row = 0; row < 9; row++) b.Draw(Game1.staminaRect,
                new Rectangle(message.IsPlayer ? r.Right + row : r.Left - row - 1, r.Y + 25 + row, 1, 17 - row * 2), surface);
        }
        else
        {
            int size = placed.Hero ? 40 : 55;
            DrawAvatar(b, message.IsPlayer ? null : customAvatar ?? npcAvatar, new Rectangle(r.X + 15, r.Y + 14, size, size), message.IsPlayer);
            string who = message.IsPlayer ? hideName ? T("我", "Me") : playerName : npcName;
            if (message.IsContinuation) who += T(" · 续", " · continued");
            cardText.Draw(b, cardText.Fit(who, 17, r.Width - 110, true), new Vector2(r.X + (placed.Hero ? 69 : 88), r.Y + 14), 17, ink * .8f, textOutputScale, true);
            x = r.X + (placed.Hero ? 20 : 88); y = r.Y + (placed.Hero ? 64 : 49);
        }
        foreach (string line in message.Lines)
        {
            cardText.Draw(b, line, new Vector2(x, y), placed.FontSize, ink, textOutputScale); y += placed.LineHeight;
        }
    }
}

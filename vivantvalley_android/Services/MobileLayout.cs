using Microsoft.Xna.Framework;

namespace VivantValley.Services;

/// <summary>Pure geometry shared by drawing, hit testing and offline layout tests.</summary>
internal static class MobileLayout
{
    public static Rectangle InlineEditor(Rectangle anchor, Rectangle viewport, Rectangle surface, Rectangle visible)
    {
        Rectangle area = Rectangle.Intersect(surface, visible);
        if (area.Width < 1 || area.Height < 1) area = surface;
        Rectangle mapped = ToDevice(anchor, viewport, surface);
        int width = Math.Clamp(mapped.Width, 1, Math.Max(1, area.Width));
        int height = Math.Clamp(mapped.Height, 1, Math.Max(1, area.Height));
        return new Rectangle(Math.Clamp(mapped.X, area.Left, area.Right - width),
            Math.Clamp(mapped.Y, area.Top, area.Bottom - height), width, height);
    }

    public static Rectangle HudButton(Rectangle safe, int controlHeight, float scale, int offsetX, int offsetY)
    {
        scale = float.IsFinite(scale) ? Math.Clamp(scale, .75f, 1.75f) : 1f;
        int h = Math.Min(safe.Height, Math.Max(48, (int)(controlHeight * scale)));
        int w = Math.Min(safe.Width, Math.Max(120, (int)(176 * scale)));
        int x = Math.Clamp(offsetX, 0, Math.Max(0, safe.Width - w));
        int y = Math.Clamp(offsetY, 0, Math.Max(0, safe.Height - h));
        return new Rectangle(safe.Right - w - x, safe.Bottom - h - y, w, h);
    }

    public static Rectangle Inset(Rectangle bounds, int padding)
    {
        int inset = Math.Clamp(padding, 0, Math.Max(0, (Math.Min(bounds.Width, bounds.Height) - 1) / 2));
        return new Rectangle(bounds.X + inset, bounds.Y + inset, Math.Max(1, bounds.Width - 2 * inset), Math.Max(1, bounds.Height - 2 * inset));
    }

    public static Rectangle Center(Rectangle available, int maxWidth)
    {
        int width = Math.Min(available.Width, maxWidth);
        return new Rectangle(available.X + (available.Width - width) / 2, available.Y, width, available.Height);
    }

    public static Rectangle ToDevice(Rectangle ui, Rectangle uiViewport, Rectangle device)
    {
        float sx = device.Width / (float)Math.Max(1, uiViewport.Width);
        float sy = device.Height / (float)Math.Max(1, uiViewport.Height);
        int left = device.X + (int)Math.Floor((ui.Left - uiViewport.X) * sx);
        int top = device.Y + (int)Math.Floor((ui.Top - uiViewport.Y) * sy);
        int right = device.X + (int)Math.Ceiling((ui.Right - uiViewport.X) * sx);
        int bottom = device.Y + (int)Math.Ceiling((ui.Bottom - uiViewport.Y) * sy);
        return Rectangle.Intersect(device, new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
    }

    public static float ClampScroll(float offset, int contentHeight, int visibleHeight)
        => Math.Clamp(offset, 0, Math.Max(0, contentHeight - visibleHeight));

    public static (Rectangle Body, Rectangle Close, Rectangle[] Actions) Form(Rectangle outer, int controlHeight, int count)
    {
        int h = Math.Clamp(controlHeight, 40, Math.Max(40, (outer.Height - 110) / 2));
        int columns = outer.Width >= 600 ? 2 : 1;
        int rows = (count + columns - 1) / columns;
        if (outer.Height - h - 38 - (rows * (h + 10) + 44) < h + 34)
        { columns = Math.Max(1, count); rows = count == 0 ? 0 : 1; }
        int footer = rows * (h + 10) + 44;
        Rectangle close = new(outer.Right - h - 12, outer.Top + 8, h, h);
        Rectangle body = new(outer.Left + 20, outer.Top + h + 26, Math.Max(1, outer.Width - 40),
            Math.Max(1, outer.Height - h - 38 - footer));
        int actionWidth = Math.Max(1, (body.Width - (columns - 1) * 12) / columns);
        var actions = new Rectangle[count];
        for (int i = 0; i < count; i++)
            actions[i] = new Rectangle(body.Left + i % columns * (actionWidth + 12),
                outer.Bottom - 12 - rows * (h + 10) + i / columns * (h + 10), actionWidth, h);
        return (body, close, actions);
    }

    public static (Rectangle Outer, Rectangle Input, Rectangle Send, Rectangle Settings, Rectangle Close, Rectangle Personality) Composer(Rectangle safe, int controlHeight, int sendWidth, bool personality = false, int personalityWidth = 140)
    {
        safe = Center(safe, 1000);
        bool narrow = safe.Width < 580;
        int h = Math.Clamp(controlHeight, 40, Math.Max(40, (safe.Height - 56) / (narrow ? 3 : 2)));
        int height = Math.Min(safe.Height, narrow ? h * 3 + 56 : h * 2 + 60);
        Rectangle outer = new(safe.X, safe.Bottom - height, safe.Width, height);
        const int gap = 16;
        sendWidth = narrow ? outer.Width - gap * 2 : Math.Min(sendWidth, outer.Width / 3);
        Rectangle send = new(safe.Right - gap - sendWidth, safe.Bottom - gap - h, sendWidth, h);
        Rectangle input = new(safe.Left + gap, narrow ? send.Y - h - gap : send.Y,
            Math.Max(1, narrow ? outer.Width - gap * 2 : send.X - gap - safe.Left - gap), h);
        int headerHeight = Math.Min(48, h);
        int settingsWidth = personality && narrow ? 80 : 104;
        Rectangle settings = new(safe.Right - gap - headerHeight - 8 - settingsWidth, outer.Y + 12, settingsWidth, headerHeight);
        Rectangle close = new(safe.Right - gap - headerHeight, outer.Y + 12, headerHeight, headerHeight);
        // Keep preferences beside Settings; reserve a small title area on narrow screens.
        int preferenceWidth = Math.Min(personalityWidth, Math.Max(1, settings.Left - safe.Left - 64));
        Rectangle personalityButton = personality ? new(settings.Left - 8 - preferenceWidth, settings.Y,
            preferenceWidth, settings.Height) : Rectangle.Empty;
        return (outer, input, send, settings, close, personalityButton);
    }
}

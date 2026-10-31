using Microsoft.Xna.Framework;

namespace VivantValley.Menus;

/// <summary>Fits the original dialogue as a whole, then maps its editing area into UI coordinates.</summary>
internal sealed record ChatComposerLayout(Rectangle Outer, Rectangle Frame, Rectangle Header,
    Rectangle Caption, Rectangle Input, Rectangle Send, Rectangle End, Rectangle Portrait, Rectangle Close,
    float Scale, Rectangle SourceFrame)
{
    public Rectangle InputHitArea => new(Input.X, Input.Y, Input.Width, Math.Max(Input.Height, Frame.Bottom - Input.Y));

    public static ChatComposerLayout Create(Rectangle available, float scale, int controlHeight,
        int sendWidth, int endWidth, bool portrait, bool hideDuplicateName = false, Rectangle? nativeBody = null)
    {
        Rectangle body = nativeBody ?? new Rectangle(0, 0, 1200, 384);
        // Same complete frame bounds as NativeReplyBox, including its original corner art.
        Rectangle source = new(body.X - 48, body.Y - 32, body.Width + 96, body.Height + 72);
        int control = Math.Min(Math.Clamp(controlHeight, 36, 64), Math.Max(28, available.Height / 5));
        const int toolbarGap = 12;
        float fittedScale = Math.Min(ConversationUiLayout.ClampScale(scale),
            Math.Min(available.Width / (float)source.Width,
                Math.Max(1, available.Height - control - toolbarGap) / (float)source.Height));
        int w = Math.Max(1, (int)(source.Width * fittedScale));
        int h = Math.Max(1, (int)(source.Height * fittedScale));
        Rectangle outer = new(available.Center.X - w / 2, available.Bottom - h, w, h);
        Rectangle Map(Rectangle rect)
        {
            int left = outer.X + (int)((rect.Left - source.Left) * fittedScale);
            int top = outer.Y + (int)((rect.Top - source.Top) * fittedScale);
            int right = outer.X + (int)((rect.Right - source.Left) * fittedScale);
            int bottom = outer.Y + (int)((rect.Bottom - source.Top) * fittedScale);
            return new(left, top, right - left, bottom - top);
        }
        Rectangle frame = Map(body);
        // Vanilla draws its portrait at width >= 642 and reserves 484 source pixels
        // on the right. Preserve that proportion instead of shrinking just the face.
        bool showPortrait = portrait && body.Width >= 642;
        int textWidth = body.Width - (showPortrait ? 484 : 16);
        Rectangle content = Map(new Rectangle(body.X + 8, body.Y + 8, textWidth - 8, body.Height - 16));
        Rectangle portraitBounds = showPortrait
            ? Map(new Rectangle(body.Right - 484, body.Y, 484, body.Height)) : Rectangle.Empty;
        int captionHeight = showPortrait && hideDuplicateName ? 0
            : Math.Min(48, Math.Max(20, content.Height / 3));
        Rectangle caption = new(content.X, content.Y, content.Width, captionHeight);
        int inputTop = caption.Bottom + (captionHeight > 0 ? 4 : 0);
        int inputHeight = Math.Min(Math.Max(24, (int)(64 * fittedScale)), content.Bottom - inputTop);
        Rectangle input = new(content.X, inputTop, content.Width, inputHeight);
        Rectangle close = new(outer.Right - control, outer.Top - toolbarGap - control, control, control);
        Rectangle header = new(outer.Left, close.Top, Math.Max(1, close.Left - outer.Left - 8), control);
        int sw = Math.Min(sendWidth, Math.Max(1, header.Width / 2 - 6));
        Rectangle send = new(header.Right - sw, header.Y, sw, control);
        Rectangle end = new(header.Left, header.Y, Math.Min(endWidth, Math.Max(1, send.Left - header.Left - 8)), control);
        return new(outer, frame, header, caption, input, send, end, portraitBounds, close, fittedScale, source);
    }
}

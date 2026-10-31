using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>Right-aligned composer actions, with optional controls in a popover on small screens.</summary>
internal sealed class ChatComposerToolbar
{
    internal sealed record Control(ClickableComponent Button, string Label, int MinWidth = 80);
    private readonly ClickableComponent more = new(Rectangle.Empty, "more");
    private List<Control> visible = new();
    private List<Control> overflow = new();
    private Rectangle[] overflowBounds = Array.Empty<Rectangle>();
    private bool expanded;

    public void Arrange(Rectangle header, Rectangle body, List<Control> optional, List<Control> actions)
    {
        foreach (Control item in visible.Concat(overflow).Concat(optional).Concat(actions))
            item.Button.bounds = Rectangle.Empty;
        expanded = false;
        more.bounds = Rectangle.Empty;
        var all = optional.Concat(actions).ToList();
        int Width(Control item) => ChatTypography.ButtonWidth(item.Label, item.MinWidth);
        bool fits = optional.Count == 0 || ChatHeaderLayout.Fits(header, all.Select(Width).ToArray());
        overflow = fits ? new() : optional;
        visible = fits ? all : new List<Control> { new(more, L.IsChinese ? "更多" : "More", 72) }.Concat(actions).ToList();
        Rectangle[] positions = ChatHeaderLayout.Arrange(header, visible.Select(Width).ToArray());
        for (int i = 0; i < visible.Count; i++) visible[i].Button.bounds = positions[i];
        overflowBounds = ChatHeaderLayout.Dropdown(body, overflow.Select(Width).ToArray(), header.Height);
    }

    public bool HandleClick(int x, int y)
    {
        if (more.containsPoint(x, y))
        {
            expanded = !expanded;
            UpdateOverflow();
            Game1.playSound("smallSelect");
            return true;
        }
        if (!expanded || overflow.Any(item => item.Button.containsPoint(x, y))) return false;
        expanded = false;
        UpdateOverflow();
        // Dismiss without moving the caret through the popover. Toolbar actions still work.
        return !visible.Any(item => item.Button.containsPoint(x, y));
    }

    public bool Dismiss()
    {
        if (!expanded) return false;
        expanded = false;
        UpdateOverflow();
        return true;
    }

    private void UpdateOverflow()
    {
        for (int i = 0; i < overflow.Count; i++)
            overflow[i].Button.bounds = expanded ? overflowBounds[i] : Rectangle.Empty;
    }

    public void Draw(SpriteBatch b)
    {
        foreach (Control item in visible) ChatTypography.DrawButton(b, item.Button, item.Label);
        if (expanded)
            foreach (Control item in overflow) ChatTypography.DrawButton(b, item.Button, item.Label);
    }
}

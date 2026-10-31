using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Scrollable NPC choices and an always reachable settings action.</summary>
internal sealed class MobileActionMenu : TouchMenu
{
    private readonly MobileFormPage form;
    public MobileActionMenu(string title, string message, IEnumerable<(string Label, Action Select)> choices, Action settings)
    {
        form = new MobileFormPage(this, title, () => "");
        form.Text(() => message);
        foreach (var choice in choices)
            form.Button(() => choice.Label, () => LeaveAndRun(choice.Select));
        form.Action(() => L.T("mobile.button.settings"), () => LeaveAndRun(settings));
        form.Action(() => L.T("mobile.close"), () => exitThisMenu());
        Reflow();
    }
    private void LeaveAndRun(Action action) { exitThisMenuNoSound(); action(); }
    private void Reflow()
    {
        Rectangle safe = MobileRuntime.SafeBounds;
        xPositionOnScreen = safe.X; yPositionOnScreen = safe.Y; width = safe.Width; height = safe.Height;
        form.Reflow();
    }
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => Reflow();
    protected override void OnTap(int x, int y, bool playSound) => form.Tap(x, y);
    protected override void OnDrag(Vector2 start, float amount) => form.Scroll(start, amount);
    public override void receiveScrollWheelAction(int direction) => form.Wheel(direction);
    public override void receiveKeyPress(Keys key)
    { if (key == Keys.Escape) exitThisMenu(); }
    public override void draw(SpriteBatch b) => form.Draw(b);
}

/// <summary>Temporary transparent menu captures the entire HUD contact, including its release.
/// Installed before the game update; removed only after the release update has finished.</summary>
internal sealed class MobileHudCaptureMenu : StardewValley.Menus.IClickableMenu
{
    private readonly Action<SpriteBatch> drawButton;
    private readonly Action cancel;
    public MobileHudCaptureMenu(Action<SpriteBatch> drawButton, Action cancel)
    { this.drawButton = drawButton; this.cancel = cancel; }
    public override bool readyToClose() => false;
    public override void receiveKeyPress(Keys key) { if (key == Keys.Escape) cancel(); }
    public override void receiveGamePadButton(Buttons button) { if (button is Buttons.B or Buttons.Back) cancel(); }
    public override void draw(SpriteBatch b) => drawButton(b);
}

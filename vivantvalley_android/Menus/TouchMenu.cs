using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Tap-on-release and scrolling shared by mobile menus; desktop dispatch remains immediate.</summary>
public abstract class TouchMenu : IClickableMenu
{
    private readonly TouchTapGesture tap = new();
    private Vector2 pressedAt;
    private double nativeInputUntil;
    private readonly NativeTouchTracker native = new();
    private bool mouseWasDown;
    private bool mouseBlocked;
    private float wheelRemainder;
    private bool disposed;
    private Rectangle lastSafeBounds;
    private readonly RasterizerState clipState = new() { ScissorTestEnable = true, CullMode = CullMode.None };
    protected virtual void OnContactPressed(Vector2 point) { }
    protected virtual void OnContactMoved(Vector2 start, Vector2 current, bool dragged) { }
    protected virtual void OnTap(int x, int y, bool playSound) { }
    protected virtual void OnDrag(Vector2 start, float amount)
    {
        wheelRemainder += amount;
        while (Math.Abs(wheelRemainder) >= 24)
        {
            int sign = Math.Sign(wheelRemainder);
            receiveScrollWheelAction(-sign * 120);
            wheelRemainder -= sign * 24;
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (!MobileRuntime.Enabled) { OnTap(x, y, playSound); return; }
        if (MobileRuntime.Now < nativeInputUntil || mouseBlocked) return;
        mouseWasDown = true;
        Press(x, y);
    }
    public override void leftClickHeld(int x, int y)
    {
        if (!MobileRuntime.Enabled || MobileRuntime.Now < nativeInputUntil || mouseBlocked) return;
        Move(x, y);
    }
    public override void releaseLeftClick(int x, int y)
    {
        if (!MobileRuntime.Enabled || MobileRuntime.Now < nativeInputUntil || mouseBlocked) return;
        mouseWasDown = false;
        Release(x, y);
    }
    private void Press(int x, int y)
    {
        if (MobileRuntime.BlockUntilRelease || MobileRuntime.Touches().Count > 1)
        { tap.Cancel(); return; }
        if (tap.IsDown) return;
        pressedAt = new(x, y); wheelRemainder = 0;
        tap.Press(x, y);
        OnContactPressed(pressedAt);
    }
    private void Move(int x, int y)
    {
        if (MobileRuntime.BlockUntilRelease || MobileRuntime.Touches().Count > 1)
        { tap.Cancel(); return; }
        float amount = tap.Move(x, y);
        OnContactMoved(pressedAt, new Vector2(x, y), tap.Dragged);
        if (amount != 0) OnDrag(pressedAt, amount);
    }
    private void Release(int x, int y)
    {
        Move(x, y);
        if (tap.Release(x, y) && !MobileRuntime.BlockUntilRelease)
        {
            MobileRuntime.TraceInput("tap dispatched", GetType().Name, $"ui={x},{y}");
            OnTap(x, y, true);
        }
    }

    internal void PollInput(TouchCollection touches, bool mouseDown, Vector2 mousePosition)
    {
        bool enabled = MobileRuntime.InputActive && !MobileRuntime.BlockUntilRelease;
        ContactUpdate contact = native.Observe(touches, enabled);
        if (touches.Count > 0 || contact.Phase != ContactPhase.None)
            nativeInputUntil = MobileRuntime.Now + 150;
        if (!enabled)
        {
            tap.Cancel(); mouseBlocked = true; mouseWasDown = mouseDown;
            return;
        }
        Vector2 ui = MobileRuntime.TouchToUi(contact.Position);
        switch (contact.Phase)
        {
            case ContactPhase.Pressed:
                tap.Cancel();
                Press((int)ui.X, (int)ui.Y);
                MobileRuntime.TraceInput("native contact started", GetType().Name);
                break;
            case ContactPhase.Moved: Move((int)ui.X, (int)ui.Y); break;
            case ContactPhase.Released: Release((int)ui.X, (int)ui.Y); break;
            case ContactPhase.Cancelled: tap.Cancel(); break;
        }
        // Native touch owns its simulated mouse events. When no native stream exists,
        // polling also supports hosts which omit menu hold/release callbacks.
        if (MobileRuntime.Now < nativeInputUntil)
        {
            mouseWasDown = mouseDown;
            if (!mouseDown) mouseBlocked = false;
            return;
        }
        if (mouseBlocked)
        {
            if (!mouseDown) mouseBlocked = false;
            mouseWasDown = mouseDown;
            return;
        }
        if (mouseDown && !mouseWasDown) Press((int)mousePosition.X, (int)mousePosition.Y);
        else if (mouseDown) Move((int)mousePosition.X, (int)mousePosition.Y);
        else if (mouseWasDown) Release((int)mousePosition.X, (int)mousePosition.Y);
        mouseWasDown = mouseDown;
    }

    internal void CancelTouch() { tap.Cancel(); native.Cancel(); mouseBlocked = true; }

    public override void update(GameTime time)
    {
        base.update(time);
        if (!MobileRuntime.Enabled) return;
        if (!MobileRuntime.InputActive) CancelTouch();
        Rectangle safe = MobileRuntime.SafeBounds;
        if (safe != lastSafeBounds)
        {
            Rectangle old = lastSafeBounds; lastSafeBounds = safe;
            gameWindowSizeChanged(old, safe);
        }
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (MobileRuntime.Enabled && button is Buttons.B or Buttons.Back) receiveKeyPress(Keys.Escape);
    }

    internal void DrawClipped(SpriteBatch b, Rectangle bounds, Action draw, float translateY = 0)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var device = b.GraphicsDevice;
        Rectangle oldClip = device.ScissorRectangle;
        Rectangle viewport = new(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
        b.End();
        device.ScissorRectangle = MobileLayout.ToDevice(bounds, viewport, device.Viewport.Bounds);
        float sx = device.Viewport.Width / (float)Math.Max(1, viewport.Width);
        float sy = device.Viewport.Height / (float)Math.Max(1, viewport.Height);
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, clipState, null,
            Matrix.CreateTranslation(0, translateY, 0) * Matrix.CreateScale(sx, sy, 1));
        try { draw(); }
        finally
        {
            b.End(); device.ScissorRectangle = oldClip;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        }
    }

    protected override void cleanupBeforeExit()
    {
        tap.Cancel();
        MobileRuntime.CloseKeyboard(this);
        if (!disposed) { disposed = true; clipState.Dispose(); }
        base.cleanupBeforeExit();
    }
}

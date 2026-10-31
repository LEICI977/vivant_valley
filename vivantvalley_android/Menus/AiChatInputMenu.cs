using VivantValley.Services;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace VivantValley.Menus;

/// <summary>A small, native-looking chat composer anchored to the bottom of the viewport.</summary>
public sealed partial class AiChatInputMenu : TouchMenu
{
    private readonly NativeChatFrame nativeFrame;
    private readonly ChatComposerToolbar toolbar = new();
    private readonly NPC? portraitNpc;
    private ChatComposerLayout layout = null!;

    private readonly string npcDisplayName;
    private readonly float conversationUiScale;
    private readonly Action<string> onSubmit;
    private readonly Action onCancel;
    private readonly Action onOpenSettings;
    private readonly Action? onShare;
    private readonly ClickableComponent shareButton = new(Rectangle.Empty, "share");
    private readonly CaretTextBox textBox;
    private readonly Action<string>? saveDraft;
    private readonly ClickableComponent sendButton = new(Rectangle.Empty, "send");
    private readonly ClickableComponent settingsButton = new(Rectangle.Empty, "settings");
    private readonly Action<string>? onPersonality;
    private readonly ClickableComponent personalityButton = new(Rectangle.Empty, "personality");
    private readonly Action<string>? onOpenSettingsDraft;
    private readonly string settingsLabel;
    private readonly string endLabel;
    private readonly Action? onEndTrip;
    private readonly ClickableComponent endTripButton = new(Rectangle.Empty, "end-trip");
    private string PersonalityButtonLabel => onEndTrip is not null && width < 580
        ? L.T("trip.personality-short") : L.T("personality.button");
    private bool closed;
    private bool submitDispatched;

    public AiChatInputMenu(
        string npcDisplayName,
        Action<string> onSubmit,
        Action onCancel,
        Action onOpenSettings,
        float conversationUiScale = 1f,
        Action? onShare = null,
        string initialText = "",
        Action<string>? saveDraft = null,
        Action<string>? onPersonality = null,
        Action? onEndTrip = null,
        string? settingsLabel = null,
        string? endLabel = null,
        Action<string>? onOpenSettingsDraft = null,
        NPC? portraitNpc = null)
    {
        this.npcDisplayName = npcDisplayName;
        this.portraitNpc = portraitNpc;
        nativeFrame = new NativeChatFrame(portraitNpc);
        this.conversationUiScale = ConversationUiLayout.ClampScale(conversationUiScale);
        this.onSubmit = onSubmit;
        this.onCancel = onCancel;
        this.onOpenSettings = onOpenSettings;
        this.onPersonality = onPersonality;
        this.onEndTrip = onEndTrip;
        this.onOpenSettingsDraft = onOpenSettingsDraft;
        this.settingsLabel = settingsLabel ?? L.T("text.df3d58c7d84b");
        this.endLabel = endLabel ?? L.T("trip.end");
        this.saveDraft = saveDraft;
        this.onShare = onShare;

        Texture2D textBoxTexture = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
        textBox = new CaretTextBox(textBoxTexture, Game1.smallFont, Game1.textColor, textLimit: 800)
        {
            DrawFrame = false,
            TextScale = ChatTypography.BodyScale,
            Selected = !MobileRuntime.IsAndroid,
        };

        // Use the same keyboard-dispatcher path as Stardew's NamingMenu. This keeps
        // Enter reliable without treating an IME candidate-confirm key as a submit.
        textBox.EnterPressed += Submit;
        textBox.Text = initialText;

        Reposition();
        if (!MobileRuntime.IsAndroid) Game1.keyboardDispatcher.Subscriber = textBox;
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        Reposition();
    }

    protected override void OnTap(int x, int y, bool playSound)
    {
        if (MobileRuntime.FinishEditing(this, () => OnTap(x, y, playSound))) return;
        if (closed) return;
        if (upperRightCloseButton?.containsPoint(x, y) == true) { Cancel(); return; }
        if (toolbar.HandleClick(x, y)) return;
        if (onEndTrip is not null && endTripButton.containsPoint(x, y))
        {
            closed = true;
            ExitComposer();
            onEndTrip();
            return;
        }
        if (onShare is not null && shareButton.containsPoint(x, y))
        {
            closed = true;
            ExitComposer();
            onShare();
            return;
        }
        if (onPersonality is not null && personalityButton.containsPoint(x, y))
        {
            string draft = textBox.Text;
            closed = true;
            ExitComposer();
            onPersonality(draft);
            return;
        }


        if (sendButton.containsPoint(x, y))
        {
            Submit();
            return;
        }

        if (settingsButton.containsPoint(x, y))
        {
            OpenSettings();
            return;
        }

        if (layout.InputHitArea.Contains(x, y))
        {
            textBox.SelectAt(x);
            if (MobileRuntime.IsAndroid) textBox.OpenMobileKeyboard(this, npcDisplayName);
            else Game1.keyboardDispatcher.Subscriber = textBox;
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (MobileRuntime.KeyboardOpen) return;
        if (MobileRuntime.IsAndroid && key != Keys.Escape) return;
        if (key == Keys.Escape)
        {
            if (toolbar.Dismiss()) return;
            Cancel();
            return;
        }

        // TextBox receives typing, Enter, and IME composition through
        // Game1.keyboardDispatcher. Avoid processing the same physical key twice.
        if (!textBox.Selected)
            base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        base.receiveGamePadButton(button);
    }

    public override void performHoverAction(int x, int y)
    {
        textBox.Hover(x, y);
        sendButton.scale = sendButton.containsPoint(x, y) ? 1.04f : 1f;
        settingsButton.scale = settingsButton.containsPoint(x, y) ? 1.04f : 1f;
        upperRightCloseButton?.tryHover(x, y, 0.2f);
    }

    public override void draw(SpriteBatch b)
    {
        if (MobileRuntime.Enabled) { DrawMobile(b); return; }
        DrawComposerBody(b);
        toolbar.Draw(b);
        b.Draw(Game1.mouseCursors, upperRightCloseButton.bounds, new Rectangle(337, 494, 12, 12), Color.White);
        if (!MobileRuntime.Enabled) drawMouse(b);
    }

    protected override void cleanupBeforeExit()
    {
        saveDraft?.Invoke(textBox.Text);
        if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, textBox))
            Game1.keyboardDispatcher.Subscriber = null;

        if (!closed)
        {
            closed = true;
            onCancel();
        }

        base.cleanupBeforeExit();
    }

    private void Submit()
    {
        if (closed || submitDispatched)
            return;

        string text = (textBox.Text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0)
        {
            Game1.playSound("cancel");
            return;
        }

        submitDispatched = true;
        closed = true;
        Game1.playSound("smallSelect");
        ExitComposer();
        onSubmit(text);
    }

    private void Cancel()
    {
        if (closed)
            return;

        closed = true;
        Game1.playSound("bigDeSelect");
        ExitComposer();
        onCancel();
    }

    private void OpenSettings()
    {
        if (closed)
            return;

        closed = true;
        Game1.playSound("smallSelect");
        ExitComposer();
        if (onOpenSettingsDraft is not null) onOpenSettingsDraft(textBox.Text); else onOpenSettings();
    }

    private void ExitComposer()
    {
        if (ReferenceEquals(Game1.activeClickableMenu, this))
            exitThisMenuNoSound();
    }

    private void Reposition()
    {
        if (MobileRuntime.Enabled) { RepositionMobile(); return; }
        ApplyLayout(new Rectangle(20, 20, Math.Max(1, Game1.uiViewport.Width - 40),
            Math.Max(1, Game1.uiViewport.Height - 40)), 44);
    }

    private void ApplyLayout(Rectangle available, int controlHeight)
    {
        nativeFrame.Reposition();
        int sendWidth = ChatTypography.ButtonWidth(L.T("text.edecf0ae6e51"), 88);
        int endWidth = ChatTypography.ButtonWidth(endLabel, 100);
        layout = ChatComposerLayout.Create(available, conversationUiScale, controlHeight, sendWidth, endWidth,
            nativeFrame.isPortraitBox(),
            hideDuplicateName: npcDisplayName == portraitNpc?.displayName, nativeBody: nativeFrame.BodyBounds);
        xPositionOnScreen = layout.Outer.X;
        yPositionOnScreen = layout.Header.Y;
        width = layout.Outer.Width;
        height = layout.Outer.Bottom - layout.Header.Y;
        textBox.X = layout.Input.X; textBox.Y = layout.Input.Y;
        textBox.Width = layout.Input.Width; textBox.Height = layout.Input.Height;
        textBox.TextScale = ChatTypography.BodyScale * Math.Max(.65f, layout.Scale);
        sendButton.bounds = layout.Send;
        endTripButton.bounds = layout.End;
        initializeUpperRightCloseButton();
        upperRightCloseButton.bounds = layout.Close;
        var optional = new List<ChatComposerToolbar.Control>();
        if (onPersonality is not null) optional.Add(new(personalityButton, PersonalityButtonLabel, 120));
        if (onShare is not null) optional.Add(new(shareButton, L.IsChinese ? "分享" : "Share", 82));
        // Dream composers historically supplied the same end action in both slots.
        if (onEndTrip is null || settingsLabel != endLabel) optional.Add(new(settingsButton, settingsLabel));
        var actions = new List<ChatComposerToolbar.Control>();
        if (onEndTrip is not null) actions.Add(new(endTripButton, endLabel, 100));
        actions.Add(new(sendButton, L.T("text.edecf0ae6e51"), 88));
        toolbar.Arrange(layout.Header, layout.Outer, optional, actions);
    }

    private void DrawComposerBody(SpriteBatch b)
    {
        nativeFrame.DrawFitted(b, layout);
        if (layout.Caption.Height > 0)
            ChatTypography.Draw(b,npcDisplayName,layout.Caption,ChatTypography.BodyScale,Game1.textColor);
        textBox.Draw(b);
        if (string.IsNullOrEmpty(textBox.Text) && !MobileRuntime.IsEditing(textBox.Bounds))
        {
            ChatTypography.Draw(b,L.T("chat.input-placeholder"),
                new Rectangle(textBox.X + 20,textBox.Y + 2,Math.Max(1,textBox.Width - 36),Math.Max(1,textBox.Height - 4)),
                textBox.TextScale,Color.Gray);
        }
    }

}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Owns AI lifecycle and external actions; the game owns reply rendering and pagination.</summary>
public sealed partial class AiStreamingDialogueMenu : TouchMenu
{
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly int maximumCharacters;
    private readonly Action onCancel;
    private readonly Action onContinue;
    private readonly Action onClosed;
    private readonly Action? onShare;
    private readonly Action? onPersonality;
    private Action? onReplay;
    private Action? onStopSpeech;

    public void SetSpeechActions(Action replay, Action stop) { onReplay = replay; onStopSpeech = stop; }
    private NativeReplyBox dialogue = null!;
    private NativeReplyLayout layout = null!;
    private ReplyPhase phase = ReplyPhase.Generating;
    private readonly ReplyHandoffTransition handoff = new();
    private Action? onApprove;
    private Action? onDecline;
    private readonly List<(ReplyAction Action, ClickableComponent Button, string Label)> buttons = new();

    public AiStreamingDialogueMenu(string npcName, string npcDisplayName, int maximumCharacters,
        Action onCancel, Action onContinue, Action onClosed,
        Action? onShare = null, Action? onPersonality = null)
    {
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.maximumCharacters = Math.Max(100, maximumCharacters);
        this.onCancel = onCancel;
        this.onContinue = onContinue;
        this.onClosed = onClosed;
        this.onShare = onShare;
        this.onPersonality = onPersonality;
        ShowWaiting();
    }

    public string NpcName => npcName;
    public bool HasReply => phase == ReplyPhase.Reply;
    public bool IsGenerating => phase == ReplyPhase.Generating;
    public bool CanContinue => phase == ReplyPhase.Reply && dialogue.ReadComplete;
    public bool IsAwaitingMoveConfirmation => phase == ReplyPhase.Confirmation;

    private void ShowWaiting() => ShowText(L.F("reply.waiting", $"正在等待 {npcDisplayName} 的回复……"), npcPortrait: false, immediate: true);

    private void ShowText(string text, bool npcPortrait = true, bool immediate = false, int portrait = 0)
    {
        NPC? npc = npcPortrait ? Game1.getCharacterFromName(npcName, mustBeVillager: false, includeEventActors: true) : null;
        dialogue = npc is null ? new NativeReplyBox(text) : new NativeReplyBox(npc, text, portrait);
        if (immediate) dialogue.showTyping = false;
        Reposition();
    }

    public void SetActionConfirmation(string confirmationText, Action onApprove, Action onDecline)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        this.onApprove = onApprove ?? throw new ArgumentNullException(nameof(onApprove));
        this.onDecline = onDecline ?? throw new ArgumentNullException(nameof(onDecline));
        phase = ReplyPhase.Confirmation;
        ShowText(string.IsNullOrWhiteSpace(confirmationText) ? L.T("reply.confirm") : confirmationText.Trim());
    }

    public void SetMoveConfirmation(string npcDisplayName, string destinationDisplayName, Action onApprove, Action onDecline)
        => SetActionConfirmation(L.F("text.e7d73e4e9360", $"和{npcDisplayName}一起去{destinationDisplayName}吗？你来带路。"), onApprove, onDecline);

    public void AppendChunk(DeepSeekStreamChunk chunk)
    {
        // The native box receives the authoritative complete reply in SetCompleted.
        // Partial model output and reasoning never replace the current page.
    }

    public void SetCompleted(string finalText, int portraitIndex = 0)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        string text = new SingleLineTextBuffer(maximumCharacters) { Text = finalText ?? string.Empty }.Text.Trim();
        if (text.Length == 0) { SetError(L.T("text.1b09e2da2e97")); return; }
        phase = ReplyPhase.Reply;
        ClearConfirmation();
        ShowText(text, portrait: portraitIndex);
    }

    public void SetError(string message)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        phase = ReplyPhase.Error;
        ClearConfirmation();
        ShowText(string.IsNullOrWhiteSpace(message) ? L.T("text.1b09e2da2e97") : message.Trim(), npcPortrait: false, immediate: true);
    }

    public void Dismiss() => Close(cancelRequest: false);

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        // Refit the same native pages: resizing must not restart typing or lose the current page.
        Reposition();
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (phase == ReplyPhase.Closed) return;
        if (phase == ReplyPhase.Closing)
        {
            if (!ReferenceEquals(Game1.activeClickableMenu, this)) { Close(false); return; }
            if (handoff.Advance(time.ElapsedGameTime, ownsMenu: true))
            {
                Close(false);
                onContinue(); // Queue only; the post-update handler opens the next menu.
                return;
            }
            dialogue.SetComposerOutro(handoff);
            Reposition();
            return;
        }
        dialogue.update(time);
        Reposition();
    }

    public override void receiveKeyPress(Keys key)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        if (key == Keys.Escape)
        {
            if (IsAwaitingMoveConfirmation) ResolveConfirmation(false);
            else Close(IsGenerating);
            return;
        }
        if (key == Keys.Enter && CanContinue)
        {
            Activate(ReplyAction.Continue);
            return;
        }
        if (key is Keys.Enter or Keys.Space) ReadPage();
    }

    public override void receiveGamePadButton(Buttons button)
    {
        if (button is Buttons.B or Buttons.Back) receiveKeyPress(Keys.Escape);
    }

    protected override void OnTap(int x, int y, bool playSound)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        // Use the last drawn hit regions: revealing the final character must not turn
        // a tap intended for reading into a newly appeared action button.
        foreach (var item in buttons.ToArray())
        {
            if (!item.Button.containsPoint(x, y)) continue;
            if (!NativeReplyPolicy.Actions(phase, dialogue.ReadComplete, onPersonality is not null, onShare is not null, onReplay is not null).Contains(item.Action)) return;
            Activate(item.Action);
            return;
        }
        if (layout.Frame.Contains(x, y)) ReadPage();
    }

    private void ReadPage()
    {
        if (phase is ReplyPhase.Generating or ReplyPhase.Closing or ReplyPhase.Closed) return;
        dialogue.ReadPage();
    }

    private void Activate(ReplyAction action)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        switch (action)
        {
            case ReplyAction.Accept: ResolveConfirmation(true); break;
            case ReplyAction.Decline: ResolveConfirmation(false); break;
            case ReplyAction.Cancel: Close(true); break;
            case ReplyAction.End: Close(false); break;
            case ReplyAction.Return:
            case ReplyAction.Continue: BeginContinuation(); break;
            case ReplyAction.Personality: Close(false); onPersonality?.Invoke(); break;
            case ReplyAction.Share: Close(false); onShare?.Invoke(); break;
            case ReplyAction.Replay: onReplay?.Invoke(); break;
        }
    }

    private void BeginContinuation()
    {
        if (!(CanContinue || phase == ReplyPhase.Error) || !handoff.Begin(dialogue.width)) return;
        onStopSpeech?.Invoke();
        phase = ReplyPhase.Closing;
        buttons.Clear();
        dialogue.SetComposerOutro(handoff);
        Game1.playSound("breathout");
    }

    private void ResolveConfirmation(bool approved)
    {
        if (!IsAwaitingMoveConfirmation || (approved && !dialogue.ReadComplete)) return;
        Action? callback = approved ? onApprove : onDecline;
        ClearConfirmation();
        phase = ReplyPhase.Generating;
        ShowWaiting();
        callback?.Invoke();
    }

    private void ClearConfirmation() { onApprove = null; onDecline = null; }

    private void Close(bool cancelRequest)
    {
        if (phase == ReplyPhase.Closed) return;
        handoff.Cancel();
        onStopSpeech?.Invoke();
        phase = ReplyPhase.Closed;
        ClearConfirmation();
        if (cancelRequest) onCancel();
        onClosed();
        if (ReferenceEquals(Game1.activeClickableMenu, this)) exitThisMenuNoSound();
    }

    protected override void cleanupBeforeExit()
    {
        onStopSpeech?.Invoke();
        handoff.Cancel();
        if (phase != ReplyPhase.Closed)
        {
            bool cancel = IsGenerating;
            Action? decline = IsAwaitingMoveConfirmation ? onDecline : null;
            phase = ReplyPhase.Closed;
            ClearConfirmation();
            if (cancel) onCancel();
            decline?.Invoke();
            onClosed();
        }
        base.cleanupBeforeExit();
    }

    public override void performHoverAction(int x, int y)
    {
        if (phase is ReplyPhase.Closing or ReplyPhase.Closed) return;
        Rectangle source = dialogue.FrameBounds;
        dialogue.performHoverAction(source.X + (int)((x - layout.Frame.X) / layout.Scale),
            source.Y + (int)((y - layout.Frame.Y) / layout.Scale));
    }

    public override void draw(SpriteBatch b)
    {
        if (phase == ReplyPhase.Closed) return;
        dialogue.DrawFitted(b, layout);
        BuildButtons();
        foreach (var item in buttons)
            NativeDialogueFrame.DrawButton(b, item.Button, item.Label,
                subdued: item.Action is ReplyAction.End or ReplyAction.Decline or ReplyAction.Cancel);
        if (!MobileRuntime.Enabled) drawMouse(b);
    }

    private void Reposition()
    {
        if (dialogue is null) return;
        Rectangle safe = MobileRuntime.SafeBounds;
        Rectangle frame = dialogue.FrameBounds;
        layout = NativeReplyLayout.Create(safe, frame.Width, frame.Height, MobileRuntime.ControlHeight);
        xPositionOnScreen = layout.Frame.X;
        yPositionOnScreen = layout.Toolbar.Y;
        width = layout.Frame.Width;
        height = layout.Frame.Bottom - yPositionOnScreen;
        // Hit regions are rebuilt when drawing, after the native reading state is known.
    }

    private void BuildButtons()
    {
        ReplyAction[] actions = NativeReplyPolicy.Actions(phase, dialogue.ReadComplete, onPersonality is not null, onShare is not null, onReplay is not null);
        string[] labels = actions.Select(action => L.T(action == ReplyAction.Personality ? "personality.button" : "reply." + action.ToString().ToLowerInvariant())).ToArray();
        Rectangle[] bounds = layout.Buttons(labels.Select(label => Math.Max(80, (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X) + 28)).ToArray());
        buttons.Clear();
        for (int i = 0; i < actions.Length; i++)
            buttons.Add((actions[i], new ClickableComponent(bounds[i], actions[i].ToString()), labels[i]));
    }
}

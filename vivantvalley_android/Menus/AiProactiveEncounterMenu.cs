using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

public sealed record AiProactiveChoice(string Id, string Text, bool IsDefer);

/// <summary>A native-looking proactive encounter with data-driven branch choices.</summary>
public sealed partial class AiProactiveEncounterMenu : TouchMenu
{
    private readonly string npcName;
    private readonly string npcDisplayName;
    private readonly DialogueTypewriter typewriter = new();
    private readonly int portraitIndex;
    private readonly float proactiveUiScale;
    private readonly IReadOnlyList<AiProactiveChoice> choices;
    private readonly Func<string, EncounterChoiceResult> onChoose;
    private readonly Action onCancel;
    private readonly Action onClosed;
    private readonly Action? onSkip;
    private readonly List<ClickableComponent> choiceButtons = new();
    private readonly ClickableComponent skipButton = new(Rectangle.Empty, "skip");
    private NativeDialogueFrameLayout frameLayout = null!;
    private Rectangle textBounds;
    private Rectangle statusBounds;
    private int firstVisibleLine;
    private bool closed;
    private bool resolutionHandled;
    private bool resolving;
    private string statusText;

    public AiProactiveEncounterMenu(
        string npcName,
        string npcDisplayName,
        string dialogueText,
        IReadOnlyList<AiProactiveChoice> choices,
        Func<string, EncounterChoiceResult> onChoose,
        Action onCancel,
        Action onClosed,
        float proactiveUiScale = 1f,
        int portraitIndex = 0,
        string? sceneStatusText = null,
        Action? onSkip = null)
    {
        this.npcName = npcName;
        this.npcDisplayName = npcDisplayName;
        this.portraitIndex = Math.Max(0, portraitIndex);
        this.proactiveUiScale = ConversationUiLayout.ClampScale(proactiveUiScale);
        statusText = string.IsNullOrWhiteSpace(sceneStatusText)
            ? L.F("text.9dd6d4561e2d", $"{npcDisplayName} 主动和你聊了起来")
            : sceneStatusText.Trim();
        typewriter.SetText(dialogueText);
        this.choices = choices?.Where(choice => choice is not null).Take(4).ToArray()
            ?? throw new ArgumentNullException(nameof(choices));
        if (this.choices.Count == 0)
            throw new ArgumentException("At least one proactive choice is required.", nameof(choices));
        this.onChoose = onChoose ?? throw new ArgumentNullException(nameof(onChoose));
        this.onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        this.onClosed = onClosed;
        this.onSkip = onSkip;
        for (int index = 0; index < this.choices.Count; index++)
            choiceButtons.Add(new ClickableComponent(Rectangle.Empty, this.choices[index].Id));
        Reposition();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        Reposition();
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (!closed)
            typewriter.Update(time.ElapsedGameTime.TotalMilliseconds);
    }

    /// <summary>Closes an interrupted scene after its owner has already handled persistence.</summary>
    public void Dismiss()
    {
        if (closed)
            return;

        resolutionHandled = true;
        Close();
    }

    protected override void OnTap(int x, int y, bool playSound)
    {
        if (MobileRuntime.Enabled && HandleMobileChoiceTap(x, y)) return;
        if (onSkip is not null && skipButton.containsPoint(x, y))
        {
            SkipScene();
            return;
        }

        if (!typewriter.IsComplete)
        {
            typewriter.RevealAll();
            Game1.playSound("smallSelect");
            return;
        }

        for (int index = 0; index < choiceButtons.Count; index++)
        {
            if (choiceButtons[index].containsPoint(x, y))
            {
                TryChoose(index);
                return;
            }
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (MobileRuntime.Enabled && key == Keys.Escape && MobileRuntime.DismissKeyboard(this)) return;
        if (key == Keys.Escape)
        {
            CancelEncounter();
            return;
        }

        if (key == Keys.Enter || key == Keys.Space)
        {
            if (!typewriter.IsComplete)
            {
                typewriter.RevealAll();
                Game1.playSound("smallSelect");
                return;
            }

            TryChoose(0);
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void receiveGamePadButton(Buttons button)
    {
        base.receiveGamePadButton(button);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        string[] lines = GetWrappedLines(out int maximumVisibleLines);
        int maximumStart = Math.Max(0, lines.Length - maximumVisibleLines);
        if (direction > 0)
            firstVisibleLine = Math.Max(0, firstVisibleLine - 1);
        else if (direction < 0)
            firstVisibleLine = Math.Min(maximumStart, firstVisibleLine + 1);
    }

    public override void performHoverAction(int x, int y)
    {
        skipButton.scale = onSkip is not null && skipButton.containsPoint(x, y) ? 1.04f : 1f;
        foreach (ClickableComponent button in choiceButtons)
            button.scale = button.containsPoint(x, y) ? 1.04f : 1f;
    }

    public override void draw(SpriteBatch b)
    {
        NativeDialogueFrame.Draw(b, frameLayout, npcName, npcDisplayName, portraitIndex);
        DrawHeader(b);
        if (MobileRuntime.Enabled) DrawClipped(b, textBounds, () => DrawDialogue(b));
        else DrawDialogue(b);
        if (MobileRuntime.Enabled) DrawMobileChoices(b);
        else if (typewriter.IsComplete)
        {
            for (int index = 0; index < choiceButtons.Count; index++)
                NativeDialogueFrame.DrawButton(
                    b,
                    choiceButtons[index],
                    choices[index].Text,
                    subdued: choices[index].IsDefer);
        }
        if (onSkip is not null)
            NativeDialogueFrame.DrawButton(b, skipButton, L.T("text.fc50a99caae0"), subdued: true);
        if (!MobileRuntime.Enabled) drawMouse(b);
    }

    protected override void cleanupBeforeExit()
    {
        if (!closed)
        {
            closed = true;
            try
            {
                if (!resolutionHandled)
                    onCancel();
            }
            finally { onClosed(); }
        }

        base.cleanupBeforeExit();
    }

    private void TryChoose(int index)
    {
        if (closed || resolutionHandled || resolving || !typewriter.IsComplete || index < 0 || index >= choices.Count)
            return;

        AiProactiveChoice choice = choices[index];
        resolving = true;
        EncounterChoiceResult result;
        try { result = onChoose(choice.Id); }
        finally { resolving = false; }
        if (!result.ClosesMenu())
        {
            statusText = L.T(result.MessageKey());
            Game1.playSound("cancel");
            return;
        }

        resolutionHandled = true;
        Close();
        if (result != EncounterChoiceResult.Completed)
            Game1.addHUDMessage(new HUDMessage(L.T(result.MessageKey()), HUDMessage.error_type));
        Game1.playSound(result != EncounterChoiceResult.Completed || choice.IsDefer ? "bigDeSelect" : "coin");
    }

    private void CancelEncounter()
    {
        if (closed || resolutionHandled || resolving)
            return;

        // Cancel works even while text is typing or the displayed record has expired.
        resolutionHandled = true;
        try { onCancel(); }
        finally { Close(); }
    }

    private void SkipScene()
    {
        if (closed || resolutionHandled || onSkip is null)
            return;

        resolutionHandled = true;
        onSkip();
        Game1.playSound("bigDeSelect");
        Close();
    }

    private void Close()
    {
        if (closed)
            return;
        closed = true;
        try { onClosed(); }
        finally
        {
            if (ReferenceEquals(Game1.activeClickableMenu, this))
                exitThisMenuNoSound();
        }
    }

    private void DrawHeader(SpriteBatch b)
    {
        NativeDialogueFrame.DrawStatus(b, statusBounds, statusText, new Color(108, 76, 54));
    }

    private void DrawDialogue(SpriteBatch b)
    {
        string[] lines = GetWrappedLines(out int maximumVisibleLines);
        firstVisibleLine = Math.Clamp(firstVisibleLine, 0, Math.Max(0, lines.Length - maximumVisibleLines));
        for (int index = 0; index < maximumVisibleLines && firstVisibleLine + index < lines.Length; index++)
        {
            b.DrawString(
                Game1.dialogueFont,
                lines[firstVisibleLine + index],
                new Vector2(textBounds.X, textBounds.Y + index * Game1.dialogueFont.LineSpacing),
                Game1.textColor);
        }
    }

    private string[] GetWrappedLines(out int maximumVisibleLines)
    {
        maximumVisibleLines = Math.Max(1, textBounds.Height / Math.Max(1, Game1.dialogueFont.LineSpacing));
        string wrapped = Game1.parseText(typewriter.VisibleText, Game1.dialogueFont, Math.Max(MobileRuntime.Enabled ? 1 : 160, textBounds.Width));
        return wrapped.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
    }

    private void Reposition()
    {
        if (MobileRuntime.Enabled) { RepositionMobile(); return; }
        int rows = (choiceButtons.Count + 1) / 2;
        frameLayout = NativeDialogueFrame.CreateLayout(
            Game1.uiViewport.Width,
            Game1.uiViewport.Height,
            proactiveUiScale,
            rows);
        Rectangle outer = frameLayout.OuterBounds;
        Rectangle contentBounds = frameLayout.ContentBounds;
        width = outer.Width;
        height = outer.Height;
        xPositionOnScreen = outer.X;
        yPositionOnScreen = outer.Y;

        const int buttonHeight = 44;
        const int rowGap = 8;
        int footerHeight = rows * buttonHeight + Math.Max(0, rows - 1) * rowGap;
        int footerY = contentBounds.Bottom - footerHeight;
        int headerRight = onSkip is null ? contentBounds.Right : contentBounds.Right - 92;
        statusBounds = new Rectangle(
            contentBounds.X,
            contentBounds.Y + 4,
            Math.Max(80, headerRight - contentBounds.X),
            Game1.smallFont.LineSpacing);
        int textY = contentBounds.Y + 38;
        textBounds = new Rectangle(
            contentBounds.X,
            textY,
            contentBounds.Width,
            Math.Max(70, footerY - textY - 10));

        const int gap = 12;
        bool singleButton = choiceButtons.Count == 1;
        int buttonWidth = singleButton
            ? Math.Min(280, contentBounds.Width)
            : Math.Max(120, (contentBounds.Width - gap) / 2);
        int gridWidth = singleButton ? buttonWidth : buttonWidth * 2 + gap;
        int buttonsX = contentBounds.X + (contentBounds.Width - gridWidth) / 2;
        for (int index = 0; index < choiceButtons.Count; index++)
        {
            int column = singleButton ? 0 : index % 2;
            int row = singleButton ? 0 : index / 2;
            choiceButtons[index].bounds = new Rectangle(
                buttonsX + column * (buttonWidth + gap),
                footerY + row * (buttonHeight + rowGap),
                buttonWidth,
                buttonHeight);
        }

        if (onSkip is not null)
        {
            // Keep the skip affordance in the header, like vanilla event UI,
            // so it never overlaps dialogue or branch choices.
            skipButton.bounds = new Rectangle(
                contentBounds.Right - 80,
                contentBounds.Y,
                80,
                36);
        }
    }
}

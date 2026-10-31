using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Edits a draft; only Save updates the farmer's preferences.</summary>
internal sealed class NpcPersonalityMenu : IClickableMenu
{
    private readonly CaretTextBox[] fields;
    private readonly string[] names = { "tone", "traits", "expression" };
    private readonly ClickableComponent save = new(Rectangle.Empty, "save");
    private readonly ClickableComponent cancel = new(Rectangle.Empty, "cancel");
    private readonly ClickableComponent reset = new(Rectangle.Empty, "reset");
    private readonly string title;
    private readonly Action<NpcPersonalitySettings> onSave;
    private readonly Action onReturn;
    private readonly Action<NpcPersonalitySettings>? onVoice;
    private readonly ClickableComponent personalityTab = new(Rectangle.Empty, "personality");
    private readonly ClickableComponent voiceTab = new(Rectangle.Empty, "voice");
    private bool closed;
    private int selected;
    private int rowHeight;
    private bool compact;
    private int labelWidth;

    public NpcPersonalityMenu(string npc, NpcPersonalitySettings current,
        Action<NpcPersonalitySettings> onSave, Action onReturn, Action<NpcPersonalitySettings>? onVoice = null)
    {
        title = L.F("speech.npcTitle", $"{npc} · NPC 设置");
        this.onVoice = onVoice;
        this.onSave = onSave; this.onReturn = onReturn;
        string[] values = { current.Tone, current.Traits, current.Expression };
        fields = values.Select(value => new CaretTextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
            Game1.smallFont, Game1.textColor, NpcPersonalitySettings.FieldLimit) { Text = value }).ToArray();
        foreach (CaretTextBox field in fields) field.EnterPressed += NextField;
        Reposition(); Select(0);
    }

    private void Select(int index)
    {
        selected = index;
        for (int i = 0; i < fields.Length; i++) fields[i].Selected = i == index;
        Game1.keyboardDispatcher.Subscriber = fields[index];
    }
    private void NextField() => Select((selected + 1) % fields.Length);
    private void Finish(bool persist)
    {
        if (closed) return;
        if (persist) onSave(new() { Tone = fields[0].Text, Traits = fields[1].Text, Expression = fields[2].Text });
        closed = true;
        exitThisMenuNoSound();
        onReturn();
    }
    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape) Finish(false);
        else if (key == Keys.Tab) NextField();
        // Text and Enter go through the keyboard dispatcher for IME compatibility.
    }
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (onVoice is not null && voiceTab.containsPoint(x,y))
        {
            var draft = new NpcPersonalitySettings { Tone=fields[0].Text, Traits=fields[1].Text, Expression=fields[2].Text };
            closed=true; exitThisMenuNoSound(); onVoice(draft); return;
        }
        if (save.containsPoint(x,y)) { Finish(true); return; }
        if (cancel.containsPoint(x,y) || upperRightCloseButton?.containsPoint(x,y) == true) { Finish(false); return; }
        if (reset.containsPoint(x,y)) { foreach (var field in fields) field.Text = string.Empty; Select(0); return; }
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Bounds.Contains(x,y)) { Select(i); fields[i].SelectAt(x); return; }
    }
    protected override void cleanupBeforeExit()
    {
        foreach (var field in fields)
        {
            field.EnterPressed -= NextField;
            if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, field)) Game1.keyboardDispatcher.Subscriber = null;
        }
        base.cleanupBeforeExit();
    }
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => Reposition();
    private void Reposition()
    {
        width = Math.Min(900, Game1.uiViewport.Width - 32);
        height = Math.Min(550, Game1.uiViewport.Height - 32);
        xPositionOnScreen = (Game1.uiViewport.Width-width)/2;
        yPositionOnScreen = (Game1.uiViewport.Height-height)/2;
        compact = height < 440;
        labelWidth = compact ? Math.Min(190, (width - 60) / 3) : 0;
        rowHeight = (height - (onVoice is null ? 194 : 224)) / 3;
        personalityTab.bounds = new(xPositionOnScreen+24,yPositionOnScreen+62,(width-60)/2,38);
        voiceTab.bounds = new(personalityTab.bounds.Right+12,personalityTab.bounds.Y,personalityTab.bounds.Width,38);
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i].X = xPositionOnScreen + 24 + (compact ? labelWidth + 12 : 0);
            fields[i].Y = yPositionOnScreen + (onVoice is null ? 112 : 142) + i*rowHeight;
            fields[i].Width = width-48 - (compact ? labelWidth + 12 : 0); fields[i].Height = 44;
        }
        int w = (width-72)/3, y = yPositionOnScreen+height-64;
        reset.bounds = new(xPositionOnScreen+24,y,w,44);
        cancel.bounds = new(reset.bounds.Right+12,y,w,44);
        save.bounds = new(cancel.bounds.Right+12,y,w,44);
        initializeUpperRightCloseButton();
    }
    private void Label(SpriteBatch b, string text, int x, int y, int available, Color color)
        => b.DrawString(Game1.smallFont, NativeDialogueFrame.FitSingleLine(text,Game1.smallFont,Math.Max(1,available)),new Vector2(x,y),color);
    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0,0,Game1.uiViewport.Width,Game1.uiViewport.Height),Color.Black*.35f);
        drawTextureBox(b,xPositionOnScreen,yPositionOnScreen,width,height,Color.White);
        Label(b,title,xPositionOnScreen+24,yPositionOnScreen+22,width-110,Game1.textColor);
        if (onVoice is null) Label(b,L.T("personality.help"),xPositionOnScreen+24,yPositionOnScreen+57,width-48,Color.Gray);
        else
        {
            NativeDialogueFrame.DrawButton(b,personalityTab,L.T("speech.personalityTab"));
            NativeDialogueFrame.DrawButton(b,voiceTab,L.T("speech.voiceTab"));
        }
        for (int i=0;i<fields.Length;i++)
        {
            var field=fields[i];
            Label(b,L.T("personality."+names[i]),compact ? xPositionOnScreen+24 : field.X,
                compact ? field.Y+8 : field.Y-26,compact ? labelWidth : field.Width,Game1.textColor);
            field.Draw(b);
            if (rowHeight >= 98) Label(b,L.T("personality.hint."+names[i]),field.X,field.Y+48,field.Width,Color.Gray);
        }
        Label(b,L.T("personality.status"),xPositionOnScreen+24,save.bounds.Y-30,width-48,Color.Gray);
        NativeDialogueFrame.DrawButton(b,reset,L.T("personality.reset"));
        NativeDialogueFrame.DrawButton(b,cancel,L.T("personality.cancel"));
        NativeDialogueFrame.DrawButton(b,save,L.T("personality.save"));
        upperRightCloseButton?.draw(b); drawMouse(b);
    }
}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

/// <summary>Scrollable form using Android's native text input and commit path.</summary>
internal sealed class NpcPersonalityMenu : TouchMenu
{
    private readonly MobileFormPage page;
    private readonly CaretTextBox[] fields;
    private readonly Action<NpcPersonalitySettings> onSave;
    private readonly Action onReturn;
    private readonly Action<NpcPersonalitySettings>? onVoice;
    private bool closed;

    public NpcPersonalityMenu(string npc, NpcPersonalitySettings current,
        Action<NpcPersonalitySettings> onSave, Action onReturn, Action<NpcPersonalitySettings>? onVoice = null)
    {
        this.onSave=onSave; this.onReturn=onReturn; this.onVoice=onVoice;
        page = new(this,L.F("speech.npcTitle", $"{npc} · NPC 设置"),()=>L.T("personality.status"));
        if (onVoice is not null)
        {
            page.Text(()=>L.T("speech.personalityTab"));
            page.Button(()=>L.T("speech.voiceTab"),OpenVoice);
        }
        page.Text(()=>L.T("personality.help"));
        string[] values={current.Tone,current.Traits,current.Expression};
        string[] names={"tone","traits","expression"};
        fields=values.Select(value=>new CaretTextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
            Game1.smallFont,Game1.textColor,NpcPersonalitySettings.FieldLimit) {Text=value}).ToArray();
        for (int i=0;i<fields.Length;i++)
        {
            string key=names[i];
            page.Field(L.T("personality."+key),fields[i]);
            page.Text(()=>L.T("personality.hint."+key));
        }
        page.Button(()=>L.T("personality.reset"),()=> { foreach(var field in fields) field.Text=string.Empty; });
        page.Action(()=>L.T("personality.save"),()=>Finish(true));
        page.Action(()=>L.T("personality.cancel"),()=>Finish(false));
        page.Reflow();
    }
    private void OpenVoice()
    {
        if (closed || onVoice is null) return;
        if (MobileRuntime.FinishEditing(this,OpenVoice)) return;
        var draft=new NpcPersonalitySettings {Tone=fields[0].Text,Traits=fields[1].Text,Expression=fields[2].Text};
        closed=true;exitThisMenuNoSound();onVoice(draft);
    }
    private void Finish(bool persist)
    {
        if (closed) return;
        if (MobileRuntime.FinishEditing(this,()=>Finish(persist))) return;
        if (persist) onSave(new() {Tone=fields[0].Text,Traits=fields[1].Text,Expression=fields[2].Text});
        closed=true; exitThisMenuNoSound(); onReturn();
    }
    protected override void OnTap(int x,int y,bool playSound) => page.Tap(x,y);
    protected override void OnDrag(Vector2 start,float amount) => page.Scroll(start,amount);
    public override void receiveScrollWheelAction(int direction) => page.Wheel(direction);
    public override void receiveKeyPress(Keys key)
    {
        if (key!=Keys.Escape) return;
        if (MobileRuntime.DismissKeyboard(this)) return;
        Finish(false);
    }
    protected override void cleanupBeforeExit()
    {
        foreach(var field in fields)
            if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber,field)) Game1.keyboardDispatcher.Subscriber=null;
        base.cleanupBeforeExit();
    }
    public override void draw(SpriteBatch b) {page.Draw(b);drawMouse(b);}
}

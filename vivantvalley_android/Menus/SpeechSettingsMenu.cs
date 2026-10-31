using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using VivantValley.Services;

namespace VivantValley.Menus;

internal sealed class SpeechSettingsMenu : TouchMenu
{
    private readonly SpeechSettingsState state;
    private readonly SpeechPlayback preview=new();
    private readonly string title;
    private readonly Action onReturn;
    private readonly Action? onPersonality;
    private MobileFormPage page=null!;
    private bool closed;

    public SpeechSettingsMenu(string title,SpeechSettingsState state,Action onReturn,Action? onPersonality=null)
    {this.title=title;this.state=state;this.onReturn=onReturn;this.onPersonality=onPersonality;BuildPage();}
    private void BuildPage()
    {
        page=new(this,title,()=>state.Status);
        if(onPersonality is not null)
        {
            page.Button(()=>L.T("speech.personalityTab"),()=>Finish(onPersonality));
            page.Text(()=>L.T("speech.voiceTab"));
        }
        page.Button(()=>L.T(state.Enabled?"speech.enabled":"speech.disabled"),state.Toggle);
        page.Button(()=>L.T(state.Language=="en"?"speech.languageEnglish":"speech.languageChinese"),()=>{preview.Stop();state.ToggleLanguage();},()=>state.CanUseEnglish||state.Language=="en");
        if(state.LanguageAvailabilityHint.Length>0)page.Text(()=>state.LanguageAvailabilityHint);
        page.Text(()=>L.F("speech.volume",$"播放音量：{Math.Round(state.Volume*100)}%"));
        page.Button(()=>L.T("speech.volumeDown"),()=>state.VolumeBy(-.1f));
        page.Button(()=>L.T("speech.volumeUp"),()=>state.VolumeBy(.1f));
        if(state.Catalog.Loaded)
        {
            page.Text(()=>L.F("speech.priceText",$"纯文字：¥{state.Catalog.PriceText(false)} / 百万 Token"));
            page.Text(()=>L.F("speech.priceVoice",$"文字＋语音总价：¥{state.Catalog.PriceText(true)} / 百万 Token"));
        }
        page.Text(()=>L.T("speech.billingHint"));page.Text(()=>L.T("speech.hostedOnly"));
        if(state.NpcName is null)page.Text(()=>L.T("speech.chooseHint"));
        else
        {
            page.Button(()=>(state.SelectedId.Length==0?"● ":"○ ")+L.T("speech.default"),()=>state.Select(""));
            if(state.Voices.Count==0)page.Text(()=>L.T("speech.noVoices"));
            foreach(var option in state.Voices)
            {
                var voice=option;
                page.Button(()=>(state.SelectedId==voice.Id?"● ":"○ ")+voice.Name+(voice.IsDefault?" · "+L.T("speech.defaultBadge"):""),()=>state.Select(voice.Id));
                if(voice.Description.Length>0)page.Text(()=>voice.Description);
                page.Button(()=>L.T(state.HasPreview(voice)?"speech.preview":"speech.noPreview"),()=>PlayPreview(voice.Id),()=>state.HasPreview(voice));
            }
        }
        page.Action(()=>L.T(state.Refreshing?"speech.loading":"speech.refresh"),state.Refresh,()=>!state.Refreshing);
        page.Action(()=>L.T("speech.back"),()=>Finish(onReturn));page.Reflow();
    }
    private void PlayPreview(string id)
    {state.PreviewStarted();preview.Start(this,token=>state.Preview(id,token),state.PreviewError);}
    public override void update(GameTime time)
    {base.update(time);if(state.Update())BuildPage();preview.Update(Game1.activeClickableMenu,state.Volume);}
    private void Finish(Action next)
    {if(closed)return;closed=true;exitThisMenuNoSound();next();}
    protected override void OnTap(int x,int y,bool playSound)=>page.Tap(x,y);
    protected override void OnDrag(Vector2 start,float amount)=>page.Scroll(start,amount);
    public override void receiveScrollWheelAction(int direction)=>page.Wheel(direction);
    public override void receiveKeyPress(Keys key){if(key==Keys.Escape)Finish(onReturn);}
    public override void gameWindowSizeChanged(Rectangle oldBounds,Rectangle newBounds)=>page.Reflow();
    protected override void cleanupBeforeExit(){preview.Dispose();state.Dispose();base.cleanupBeforeExit();}
    public override void draw(SpriteBatch b){page.Draw(b);drawMouse(b);}
}

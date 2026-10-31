using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Services;

namespace VivantValley.Menus;

internal sealed class SpeechSettingsMenu : IClickableMenu
{
    private sealed record Row(string Text,Rectangle Bounds,Action? Click,bool Enabled);
    private readonly SpeechSettingsState state;
    private readonly SpeechPlayback preview=new();
    private readonly string title;
    private readonly Action onReturn;
    private readonly Action? onPersonality;
    private readonly List<Row> rows=new();
    private readonly ClickableComponent back=new(Rectangle.Empty,"back"), refresh=new(Rectangle.Empty,"refresh"), personality=new(Rectangle.Empty,"personality"), voiceTab=new(Rectangle.Empty,"voice");
    private Rectangle body;
    private int scroll, contentHeight;
    private bool closed;

    public SpeechSettingsMenu(string title,SpeechSettingsState state,Action onReturn,Action? onPersonality=null)
    { this.title=title;this.state=state;this.onReturn=onReturn;this.onPersonality=onPersonality;Reposition(); }

    private void Reposition()
    {
        width=Math.Min(960,Game1.uiViewport.Width-32);height=Math.Min(760,Game1.uiViewport.Height-32);
        xPositionOnScreen=(Game1.uiViewport.Width-width)/2;yPositionOnScreen=(Game1.uiViewport.Height-height)/2;
        body=new(xPositionOnScreen+28,yPositionOnScreen+(onPersonality is null?76:120),width-56,height-(onPersonality is null?196:240));
        back.bounds=new(xPositionOnScreen+28,yPositionOnScreen+height-64,(width-68)/2,44);
        refresh.bounds=new(back.bounds.Right+12,back.bounds.Y,(width-68)/2,44);
        personality.bounds=new(body.Left,yPositionOnScreen+62,(body.Width-12)/2,40);
        voiceTab.bounds=new(personality.bounds.Right+12,personality.bounds.Y,personality.bounds.Width,40);
        initializeUpperRightCloseButton();BuildRows();
    }
    private void BuildRows()
    {
        rows.Clear();int y=body.Top;
        void Text(string value)
        {
            string wrapped=Game1.parseText(value,Game1.smallFont,Math.Max(80,body.Width-12));
            foreach(string line in wrapped.Split('\n'))
            { rows.Add(new(line,new(body.X,y,body.Width,30),null,true));y+=32; }
            y+=6;
        }
        void Button(string label,Action click,bool enabled=true)
        {rows.Add(new(label,new(body.X,y,body.Width,44),click,enabled));y+=54;}
        Button(L.T(state.Enabled?"speech.enabled":"speech.disabled"),state.Toggle);
        Button(L.T(state.Language=="en"?"speech.languageEnglish":"speech.languageChinese"),()=>{preview.Stop();state.ToggleLanguage();},state.CanUseEnglish||state.Language=="en");
        if(state.LanguageAvailabilityHint.Length>0)Text(state.LanguageAvailabilityHint);
        Text(L.F("speech.volume",$"播放音量：{Math.Round(state.Volume*100)}%"));
        Button(L.T("speech.volumeDown"),()=>state.VolumeBy(-.1f));
        Button(L.T("speech.volumeUp"),()=>state.VolumeBy(.1f));
        if(state.Catalog.Loaded)
        {
            Text(L.F("speech.priceText",$"纯文字：¥{state.Catalog.PriceText(false)} / 百万 Token"));
            Text(L.F("speech.priceVoice",$"文字＋语音总价：¥{state.Catalog.PriceText(true)} / 百万 Token"));
        }
        Text(L.T("speech.billingHint"));Text(L.T("speech.hostedOnly"));
        if(state.NpcName is null) Text(L.T("speech.chooseHint"));
        else
        {
            Button((state.SelectedId.Length==0?"● ":"○ ")+L.T("speech.default"),()=>state.Select(""));
            if(state.Voices.Count==0) Text(L.T("speech.noVoices"));
            foreach(var option in state.Voices)
            {
                var voice=option;
                Button((state.SelectedId==voice.Id?"● ":"○ ")+voice.Name+(voice.IsDefault?" · "+L.T("speech.defaultBadge"):""),()=>state.Select(voice.Id));
                if(voice.Description.Length>0) Text(voice.Description);
                Button(L.T(state.HasPreview(voice)?"speech.preview":"speech.noPreview"),()=>PlayPreview(voice.Id),state.HasPreview(voice));
            }
        }
        contentHeight=y-body.Top;scroll=Math.Clamp(scroll,0,Math.Max(0,contentHeight-body.Height));
    }
    private void PlayPreview(string id)
    { state.PreviewStarted();preview.Start(this,token=>state.Preview(id,token),state.PreviewError); }
    public override void update(GameTime time)
    {base.update(time);if(state.Update())BuildRows();preview.Update(Game1.activeClickableMenu,state.Volume);}
    public override void receiveScrollWheelAction(int direction) => scroll=Math.Clamp(scroll-direction/3,0,Math.Max(0,contentHeight-body.Height));
    public override void receiveKeyPress(Keys key)
    {
        if(key==Keys.Escape) Finish(onReturn);
        else if(key is Keys.PageDown or Keys.Down) receiveScrollWheelAction(-120);
        else if(key is Keys.PageUp or Keys.Up) receiveScrollWheelAction(120);
    }
    public override void receiveLeftClick(int x,int y,bool playSound=true)
    {
        if(back.containsPoint(x,y)||upperRightCloseButton?.containsPoint(x,y)==true){Finish(onReturn);return;}
        if(refresh.containsPoint(x,y)){state.Refresh();return;}
        if(onPersonality is not null&&personality.containsPoint(x,y)){Finish(onPersonality);return;}
        if(!body.Contains(x,y))return;
        foreach(var row in rows)
        {
            var bounds=row.Bounds;bounds.Y-=scroll;
            if(bounds.Top>=body.Top&&bounds.Bottom<=body.Bottom&&bounds.Contains(x,y)&&row.Click is not null&&row.Enabled)
            {row.Click();BuildRows();return;}
        }
    }
    private void Finish(Action next)
    {if(closed)return;closed=true;exitThisMenuNoSound();next();}
    protected override void cleanupBeforeExit()
    {preview.Dispose();state.Dispose();base.cleanupBeforeExit();}
    public override void gameWindowSizeChanged(Rectangle oldBounds,Rectangle newBounds)=>Reposition();
    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect,new Rectangle(0,0,Game1.uiViewport.Width,Game1.uiViewport.Height),Color.Black*.35f);
        drawTextureBox(b,xPositionOnScreen,yPositionOnScreen,width,height,Color.White);
        b.DrawString(Game1.smallFont,NativeDialogueFrame.FitSingleLine(title,Game1.smallFont,width-110),new Vector2(body.X,yPositionOnScreen+24),Game1.textColor);
        if(onPersonality is not null){NativeDialogueFrame.DrawButton(b,personality,L.T("speech.personalityTab"));NativeDialogueFrame.DrawButton(b,voiceTab,L.T("speech.voiceTab"));}
        foreach(var row in rows)
        {
            var bounds=row.Bounds;bounds.Y-=scroll;
            if(bounds.Top<body.Top||bounds.Bottom>body.Bottom)continue;
            if(row.Click is null)b.DrawString(Game1.smallFont,row.Text,new Vector2(bounds.X,bounds.Y),Game1.textColor);
            else NativeDialogueFrame.DrawButton(b,new ClickableComponent(bounds,"row"),NativeDialogueFrame.FitSingleLine(row.Text,Game1.smallFont,bounds.Width-24),subdued:!row.Enabled);
        }
        string hint=state.Status+(contentHeight>body.Height?" · "+L.T("speech.scrollHint"):"");
        b.DrawString(Game1.smallFont,NativeDialogueFrame.FitSingleLine(hint,Game1.smallFont,body.Width),new Vector2(body.X,back.bounds.Y-38),Color.Gray);
        NativeDialogueFrame.DrawButton(b,back,L.T("speech.back"));NativeDialogueFrame.DrawButton(b,refresh,L.T(state.Refreshing?"speech.loading":"speech.refresh"),subdued:state.Refreshing);
        upperRightCloseButton?.draw(b);drawMouse(b);
    }
}

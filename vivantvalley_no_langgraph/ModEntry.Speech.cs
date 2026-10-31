using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using VivantValley.Menus;
using VivantValley.Services;

namespace VivantValley;

public sealed partial class ModEntry
{
    private HostedSpeechClient speechClient = null!;
    private readonly PerScreen<SpeechPlayback> speechPlayback = new(() => new());
    private Task<SpeechCatalog>? speechCatalogRefresh;

    private void RefreshSpeechCatalog()
    {
        string token = config.Ai.Hosted.ApiKey?.Trim() ?? "";
        if (token.Length == 0 || speechCatalogRefresh is { IsCompleted:false }) return;
        speechCatalogRefresh = speechClient.RefreshAsync(token);
    }
    private void UpdateNpcSpeech()
    {
        if (speechCatalogRefresh is { IsCompleted:true } task)
        {
            speechCatalogRefresh=null;
            try { _=task.GetAwaiter().GetResult(); }
            catch(Exception ex) { Monitor.Log("Speech catalog unavailable: "+ex.GetType().Name,LogLevel.Debug); }
        }
        if (!config.EnableNpcSpeech || !Context.IsWorldReady || currentAiProfile.Provider != AiProviderNames.Hosted)
            speechPlayback.Value.Stop();
        else speechPlayback.Value.Update(Game1.activeClickableMenu,config.NpcSpeechVolume);
    }
    private void StopNpcSpeech() => speechPlayback.Value.Stop();
    private void StopAllNpcSpeech()
    {
        foreach(var screen in speechPlayback.GetActiveValues()) screen.Value.Stop();
    }
    private static string SelectedNpcVoice(Farmer player,string npc)
        => player.modData.TryGetValue(NpcVoicePreference.StorageKey(npc),out string value) ? value : "";
    private SpeechTurn CaptureSpeechTurn(string npc,string id)
    {
        var profile=Volatile.Read(ref currentAiProfile);
        bool hosted=profile.Provider==AiProviderNames.Hosted;
        string token=hosted?profile.ApiKey:"";
        SpeechCatalog catalog=speechClient.CatalogFor(token);
        var voice=catalog.Resolve(npc,Context.IsWorldReady?SelectedNpcVoice(Game1.player,npc):"");
        return new(id,npc,token,hosted&&config.EnableNpcSpeech&&catalog.Loaded&&catalog.Enabled&&voice is not null
            &&(config.NpcSpeechLanguage!="en"||catalog.SpeechLanguages.Contains("en")),voice?.Id,
            catalog.SpeechLanguages.Contains("en")?(config.NpcSpeechLanguage=="en"?"en":"zh"):null);
    }
    private void PlayNpcSpeech(SpeechTurn? turn,string reply,IClickableMenu? owner)
    {
        if(turn is not {Enabled:true} || !config.EnableNpcSpeech || owner is null || !ReferenceEquals(owner,Game1.activeClickableMenu))return;
        speechPlayback.Value.StartStream(owner,(audio,cancellation)=>speechClient.StreamAsync(turn,reply,audio,cancellation),error=>
        {
            Monitor.Log("NPC speech: "+(error is SpeechRequestException known?known.Code:error.GetType().Name),LogLevel.Debug);
            ShowHud(error is SpeechRequestException failure?failure.Message:L.T("speech.failed"));
        });
    }
    private void SaveSpeechSettings(bool enabled,float volume)
    {
        config.EnableNpcSpeech=enabled;config.NpcSpeechVolume=float.IsFinite(volume)?Math.Clamp(volume,0f,1f):.8f;
        if(!enabled)StopAllNpcSpeech();
        try {Helper.WriteConfig(config);}
        catch(Exception ex){Monitor.Log("Speech settings save failed: "+ex.GetType().Name,LogLevel.Warn);ShowHud(L.T("speech.saveFailed"));}
    }
    private SpeechSettingsState SpeechSettings(string? npc,Farmer? player=null)
        => new(speechClient,config.Ai.Hosted.ApiKey??"",npc,
            ()=>config.EnableNpcSpeech,()=>config.NpcSpeechVolume,SaveSpeechSettings,
            ()=>npc is not null&&player is not null?SelectedNpcVoice(player,npc):"",
            id=>
            {
                if(npc is null||player is null||!Context.IsWorldReady||!ReferenceEquals(player,Game1.player))return;
                string key=NpcVoicePreference.StorageKey(npc);
                if(id.Length==0)player.modData.Remove(key);else player.modData[key]=id;
            },()=>config.NpcSpeechLanguage,language=>
            {
                config.NpcSpeechLanguage=language;SaveSpeechSettings(config.EnableNpcSpeech,config.NpcSpeechVolume);
            });
    private void OpenSpeechSettings()
    {
        StopNpcSpeech();
        Game1.activeClickableMenu=new SpeechSettingsMenu(L.T("speech.title"),SpeechSettings(null),OpenHostedAccountSettings);
    }
    private string SpeechPriceSummary()
    {
        var catalog=speechClient.CatalogFor(config.Ai.Hosted.ApiKey??"");
        return catalog.Loaded?L.F("speech.accountPrices",$"纯文字 ¥{catalog.PriceText(false)} / 文字＋语音 ¥{catalog.PriceText(true)}，每百万 Token")
            :L.T("speech.loadingPrices");
    }
}

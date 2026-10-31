namespace VivantValley.Services;

/// <summary>Shared settings behavior for desktop and Android; UI and game data stay on the main thread.</summary>
public sealed class SpeechSettingsState : IDisposable
{
    private readonly HostedSpeechClient client;
    private readonly string token;
    private readonly Func<bool> enabled;
    private readonly Func<float> volume;
    private readonly Action<bool,float> save;
    private readonly Func<string> language;
    private readonly Action<string> saveLanguage;
    public string Language => language()=="en"?"en":"zh";
    public bool CanUseEnglish => Catalog.SpeechLanguages.Contains("en");
    public string LanguageAvailabilityHint => !Catalog.Loaded ? L.T("speech.catalogUnavailable")
        : CanUseEnglish ? "" : L.T("speech.languageUnavailable");
    public void ToggleLanguage() { if (!CanUseEnglish && Language!="en") return;saveLanguage(Language=="en"?"zh":"en"); }
    public bool HasPreview(NpcVoiceOption voice) => Language=="en"?voice.HasEnglishPreview:voice.HasPreview;
    private readonly Func<string> selected;
    private readonly Action<string> select;
    private readonly CancellationTokenSource cancellation = new();
    private Task<SpeechCatalog>? refresh;
    public string? NpcName { get; }
    public SpeechCatalog Catalog { get; private set; }
    public string Status { get; private set; } = "";
    public bool Enabled => enabled();
    public float Volume => Math.Clamp(volume(),0f,1f);
    public bool Refreshing => refresh is not null;
    public string SelectedId => selected();
    public IReadOnlyList<NpcVoiceOption> Voices => Catalog.ForNpc(NpcName);

    public SpeechSettingsState(HostedSpeechClient client, string token, string? npc,
        Func<bool> enabled, Func<float> volume, Action<bool,float> save, Func<string> selected, Action<string> select, Func<string>? language = null, Action<string>? saveLanguage = null)
    {
        this.client=client;this.token=token;NpcName=npc;this.enabled=enabled;this.volume=volume;
        this.language=language??(()=>"zh");this.saveLanguage=saveLanguage??(_=>{});
        this.save=save;this.selected=selected;this.select=select;
        Catalog=client.CatalogFor(token);
        Refresh();
    }
    public void Refresh()
    {
        if (refresh is not null) return;
        if (string.IsNullOrWhiteSpace(token)) { Status=L.T("speech.loginRequired");return; }
        Status=L.T("speech.loading");
        refresh=client.RefreshAsync(token,cancellation.Token);
    }
    public bool Update()
    {
        if (refresh is not { IsCompleted:true } task) return false;
        refresh=null;
        try
        {
            Catalog=task.GetAwaiter().GetResult();
            Status=Catalog.Enabled ? L.T("speech.savedHint") : L.T("speech.serverDisabled");
            if (NpcName is not null && SelectedId.Length>0 && !Voices.Any(v=>v.Id==SelectedId))
            { select("");Status=L.T("speech.voiceRemoved"); }
        }
        catch (Exception ex) { Status=ex is SpeechRequestException ? ex.Message : L.T("speech.refreshFailed"); }
        return true;
    }
    public void Toggle()
    {
        save(!Enabled,Volume);
        Status=Enabled && !Catalog.Loaded ? L.T("speech.catalogUnavailable")
            : Enabled && !Catalog.Enabled ? L.T("speech.serverDisabled") : L.T("speech.savedHint");
    }
    public void VolumeBy(float delta) {save(Enabled,Math.Clamp(Volume+delta,0f,1f));Status=L.T("speech.savedHint");}
    public void Select(string id)
    {
        if (id.Length>0 && !Voices.Any(v=>v.Id==id)) return;
        select(id);Status=L.T("speech.savedHint");
    }
    public Task<byte[]> Preview(string id,CancellationToken token) => client.PreviewAsync(this.token,id,token,CanUseEnglish?Language:null);
    public void PreviewError(Exception error) => Status=error is SpeechRequestException ? error.Message : L.T("speech.previewFailed");
    public void PreviewStarted() => Status=L.T("speech.previewLoading");
    public void Dispose()
    {
        cancellation.Cancel();
        if (refresh is { } task) _=task.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        cancellation.Dispose();
    }
}

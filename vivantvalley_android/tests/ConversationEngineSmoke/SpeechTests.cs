using System.Net;
using System.Text;
using System.Text.Json;
using VivantValley;
using VivantValley.Menus;
using VivantValley.Services;

internal static class SpeechTests
{
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    public static async Task RunAsync()
    {
        string voiceId=Guid.NewGuid().ToString(),second=Guid.NewGuid().ToString();
        var catalog=new SpeechCatalog{Loaded=true,Enabled=true,TextPriceMicros=2345678,SpeechPriceMicros=5000000,
            SpeechLanguages=new(){"zh","en"},Voices=new(){new(){Id=voiceId,NpcName="Abigail",Name="Calm",Description="Warm and quiet",IsDefault=true},
                new(){Id=second,NpcName="Abigail",Name="Bright"},new(){Id=Guid.NewGuid().ToString(),NpcName="Haley",Name="Soft"}}};
        Check(catalog.Resolve("Abigail",second)?.Id==second,"Selected NPC voice was ignored.");
        Check(catalog.Resolve("Abigail","removed")?.Id==voiceId,"Disabled/missing selection must return to default.");
        Check(catalog.Resolve("abigail",null) is null,"NPC routing must preserve case.");
        Check(catalog.PriceText(false)=="2.345678","Published price lost precision.");
        Check(new ModConfig().EnableNpcSpeech==false,"Voice must be opt-in.");
        Check(NpcVoicePreference.StorageKey("A/B")!=NpcVoicePreference.StorageKey("A%2FB"),"Voice preference keys collide.");
        var context=new ConversationRequestContext{TurnId="turn-1",VoiceEnabled=true,VoiceOptionId=voiceId,Mode="dream",SpeechLanguage="en"};
        using(var payload=JsonDocument.Parse(JsonSerializer.Serialize(context.ToHostedPayload("Abigail","conversation"))))
        {
            Check(payload.RootElement.GetProperty("speech_language").GetString()=="en","Missing frozen speech language.");
            Check(payload.RootElement.GetProperty("turn_id").GetString()=="turn-1","Missing round identity.");
            Check(payload.RootElement.GetProperty("voice_enabled").GetBoolean(),"Missing voice mode.");
            Check(payload.RootElement.GetProperty("speech_performance").GetBoolean(),"Missing performance capability.");
            Check(payload.RootElement.GetProperty("voice_option_id").GetString()==voiceId,"Missing voice selection.");
        }
        using(var off=JsonDocument.Parse(JsonSerializer.Serialize(new ConversationRequestContext{VoiceEnabled=false,SpeechLanguage="en"}.ToHostedPayload("Abigail","conversation"))))
            Check(off.RootElement.GetProperty("speech_language").ValueKind==JsonValueKind.Null,"Voice-off leaked language requirement.");
        // Real wire shape: retained preview bytes can produce null flags on older servers.
        string wireCatalog="{\"enabled\":true,\"text_price_micros_per_1m\":2000000,\"speech_price_micros_per_1m\":5000000,\"voices\":[{\"id\":\""+voiceId+"\",\"npc_name\":\"Abigail\",\"name\":\"Legacy\",\"has_preview\":null,\"has_preview_en\":null}]}";
        var legacy=JsonSerializer.Deserialize<SpeechCatalog>(wireCatalog)!;
        Check(!legacy.Voices[0].HasPreview&&!legacy.Voices[0].HasEnglishPreview,"Null preview flags must mean unavailable.");
        Check(legacy.SpeechLanguages.Count==0,"Old servers must not gain unsupported language capabilities.");
        foreach(string flag in new[]{"true","false"})
        {
            var flags=JsonSerializer.Deserialize<NpcVoiceOption>("{\"has_preview\":"+flag+",\"has_preview_en\":"+flag+"}")!;
            Check(flags.HasPreview==(flag=="true")&&flags.HasEnglishPreview==flags.HasPreview,"Boolean preview flags changed meaning.");
        }
        Check(!JsonSerializer.Deserialize<NpcVoiceOption>("{}")!.HasPreview,"Omitted legacy flags must remain false.");
        var handler=new Handler();using var http=new HttpClient(handler);
        var client=new HostedSpeechClient(http,"https://hosted.example");
        using(var bootstrap=JsonDocument.Parse("{\"wallet\":{\"available_micros\":1234567},\"speech\":{\"voices\":\"invalid\"}}"))
        {
            Check(!client.AcceptBootstrap(bootstrap.RootElement,"account-one"),"Malformed optional speech metadata must be rejected without aborting bootstrap.");
            Check(bootstrap.RootElement.GetProperty("wallet").GetProperty("available_micros").GetInt64()==1234567,"Speech failure blocked balance parsing.");
            Check(!client.CatalogFor("account-one").Loaded,"Malformed catalog was marked loaded.");
        }
        using(var bootstrap=JsonDocument.Parse("{}"))Check(!client.AcceptBootstrap(bootstrap.RootElement,"account-one"),"Pre-speech bootstrap must remain supported.");
        using(var bootstrap=JsonDocument.Parse("{\"speech\":null}"))Check(!client.AcceptBootstrap(bootstrap.RootElement,"account-one"),"Null catalog was marked loaded.");
        handler.Response=()=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(wireCatalog,Encoding.UTF8,"application/json")};
        await client.RefreshAsync("legacy-account");
        Check(client.CatalogFor("legacy-account").Loaded,"Legacy catalog did not load.");
        handler.Response=()=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"voices\":\"invalid\"}",Encoding.UTF8,"application/json")};
        bool preference=false;
        using(var settings=new SpeechSettingsState(client,"fresh-account",null,()=>preference,()=>.8f,(v,_)=>preference=v,()=>"",_=>{}))
        {
            for(int i=0;i<100&&settings.Refreshing;i++){await Task.Yield();settings.Update();}
            Check(!settings.Catalog.Loaded&&settings.LanguageAvailabilityHint==L.T("speech.catalogUnavailable"),"Failed sync was mislabeled as unsupported language.");
            settings.Toggle();Check(preference,"Local voice preference could not be saved before synchronization.");
            settings.Toggle();Check(!preference,"Voice preference could not be disabled after failed synchronization.");
        }
        handler.Response=()=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(catalog),Encoding.UTF8,"application/json")};
        await client.RefreshAsync("account-one");
        Check(client.CatalogFor("account-one").Voices.Count==3,"Catalog descriptions/options were lost.");
        Check(!client.CatalogFor("account-two").Loaded,"Catalog leaked across account sessions.");
        bool enabled=false;float volume=.8f;string selected="removed";
        using(var settings=new SpeechSettingsState(client,"account-one","Abigail",()=>enabled,()=>volume,(e,v)=>{enabled=e;volume=v;},()=>selected,id=>selected=id))
        {
            for(int i=0;i<100&&settings.Refreshing;i++){await Task.Yield();settings.Update();}
            Check(selected=="","Stale saved voice was not cleared.");settings.Toggle();Check(enabled,"Valid voice toggle failed.");
            settings.VolumeBy(4);Check(volume==1,"Volume exceeded device range.");settings.Select(second);Check(selected==second,"Voice preference was not stored.");
        }
        string language="zh";
        using(var settings=new SpeechSettingsState(client,"account-one","Abigail",()=>true,()=>.8f,(_,_)=>{},()=>voiceId,_=>{},()=>language,x=>language=x))
        {
            Check(settings.CanUseEnglish,"Modern catalog lost language capability.");settings.ToggleLanguage();Check(language=="en","Language selection was not saved.");
            Check(!settings.HasPreview(new NpcVoiceOption{HasPreview=true}),"Chinese preview exposed for English selection.");
        }
        byte[] sample=new byte[480];Encoding.ASCII.GetBytes("RIFF").CopyTo(sample,0);Encoding.ASCII.GetBytes("WAVE").CopyTo(sample,8);
        handler.Response=()=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(sample)};
        await client.PreviewAsync("account-one",voiceId,CancellationToken.None,"en");
        Check(handler.Uri.EndsWith("?language=en"),"Wrong preview language.");
        var turn=new SpeechTurn("round-2","Abigail","session-secret",true,voiceId);
        string Audio(byte[] data)=>JsonSerializer.Serialize(new{type="audio",sample_rate=24000,channels=1,pcm=Convert.ToBase64String(data)})+"\n";
        string wire=Audio(new byte[240])+Audio(new byte[240])+"{\"type\":\"done\",\"pcm_bytes\":480}\n";
        handler.Response=()=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(wire,Encoding.UTF8,"application/x-ndjson")};
        var received=new List<byte[]>();await client.StreamAsync(turn,"（轻声）你好。",received.Add,CancellationToken.None);
        Check(received.Sum(x=>x.Length)==480,"PCM stream lost samples.");
        Check(handler.Authorization=="Bearer session-secret"&&handler.Uri=="https://hosted.example/api/v1/mod/speech/turns/round-2?stream=1","Speech request was not authenticated to our backend.");
        using(var body=JsonDocument.Parse(handler.Body))Check(body.RootElement.GetProperty("reply").GetString()=="（轻声）你好。","Displayed reply was modified before server verification.");
        Check(!handler.Body.Contains("session-secret")&&!handler.Body.Contains(voiceId),"Audio request leaked session or unvalidated voice into JSON.");
        wire=Audio(new byte[480]);bool failed=false;
        try{await client.StreamAsync(turn,"reply",_=>{},CancellationToken.None);}catch(IOException){failed=true;}
        Check(failed,"A stream without completion was accepted as successful.");
        wire=Audio(new byte[480])+"{\"type\":\"error\",\"code\":\"speech_provider_failed\",\"message\":\"退款\"}\n";failed=false;
        try{await client.StreamAsync(turn,"reply",_=>{},CancellationToken.None);}catch(SpeechRequestException e){failed=e.Code=="speech_provider_failed";}
        Check(failed,"Partial synthesis error was silently accepted.");
        using(var cancel=new CancellationTokenSource())
        {
            wire=Audio(new byte[480])+"{\"type\":\"done\",\"pcm_bytes\":480}\n";failed=false;
            try{await client.StreamAsync(turn,"reply",_=>cancel.Cancel(),cancel.Token);}catch(OperationCanceledException){failed=true;}
            Check(failed,"A cancelled menu accepted further stream data.");
        }
        Check(NativeReplyPolicy.Actions(ReplyPhase.Reply,false,true,true,true).Contains(ReplyAction.Replay),"The player cannot replay a completed reply.");
        Check(!NativeReplyPolicy.Actions(ReplyPhase.Generating,false,true,true,true).Contains(ReplyAction.Replay),"Unfinished text exposed speech replay.");
        Console.WriteLine("Speech contracts: account isolation, catalog/defaults, exact prices, opt-in preferences, authenticated PCM stream, interruption and cancellation passed.");
    }
    private sealed class Handler:HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response=null!;
        public string Body="",Authorization="",Uri="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();Uri=request.RequestUri!.ToString();Authorization=request.Headers.Authorization?.ToString()??"";
            Body=request.Content is null?"":await request.Content.ReadAsStringAsync(cancellationToken);
            return Response();
        }
    }
}

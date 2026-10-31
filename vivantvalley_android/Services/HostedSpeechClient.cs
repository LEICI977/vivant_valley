using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VivantValley.Services;

/// <summary>Authenticated hosted audio; no Breeze credentials or arbitrary audio URLs reach the game.</summary>
public sealed class HostedSpeechClient(HttpClient http, string origin)
{
    private readonly object gate = new();
    private string catalogToken = "";
    private SpeechCatalog catalog = new();
    private const int MaximumAudioBytes = 24 * 1024 * 1024 + 44;

    public SpeechCatalog CatalogFor(string token)
    {
        lock (gate) return token == catalogToken ? catalog : new();
    }
    public void Clear() { lock (gate) { catalogToken = ""; catalog = new(); } }
    // Optional speech metadata must never prevent account/model/balance synchronization.
    public bool AcceptBootstrap(JsonElement root, string token)
    {
        if (!root.TryGetProperty("speech", out var value)) return false;
        try { AcceptCatalog(value, token); return true; }
        catch (JsonException) { return false; }
    }
    private SpeechCatalog AcceptCatalog(JsonElement value, string token)
    {
        var parsed = value.Deserialize<SpeechCatalog>() ?? throw new JsonException("Missing speech catalog.");
        parsed.Voices ??= new();
        parsed.SpeechLanguages ??= new();
        parsed.Voices = parsed.Voices.Where(v => v is not null && Guid.TryParse(v.Id,out _) && !string.IsNullOrWhiteSpace(v.NpcName)
            && !string.IsNullOrWhiteSpace(v.Name)).ToList();
        parsed.Loaded = true;
        lock (gate) { catalogToken = token; catalog = parsed; }
        return parsed;
    }
    public async Task<SpeechCatalog> RefreshAsync(string token, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(HttpMethod.Get, "/api/v1/mod/speech/catalog", token);
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await Failure(response).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        return AcceptCatalog(document.RootElement, token);
    }
    public Task<byte[]> PreviewAsync(string token, string id, CancellationToken cancellation, string? language = null)
        => AudioAsync(token, "/api/v1/mod/speech/voices/" + Uri.EscapeDataString(id) + "/preview" + (language is null ? "" : "?language="+Uri.EscapeDataString(language)), null, cancellation);
    public Task<byte[]> SpeakAsync(SpeechTurn turn, string reply, CancellationToken cancellation)
        => AudioAsync(turn.Token, "/api/v1/mod/speech/turns/" + Uri.EscapeDataString(turn.Id), reply, cancellation);

    public async Task StreamAsync(SpeechTurn turn,string reply,Action<byte[]> audio,CancellationToken cancellation)
    {
        using var language=L.BeginRequest();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(280));
        int attempts=0,bytes=0;
        while(true)
        {
            try
            {
                using var request=Request(HttpMethod.Post,"/api/v1/mod/speech/turns/"+Uri.EscapeDataString(turn.Id)+"?stream=1",turn.Token);
                request.Content=new StringContent(JsonSerializer.Serialize(new {reply}),Encoding.UTF8,"application/json");
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode)
                {
                    var failure=await Failure(response).ConfigureAwait(false);
                    if(failure.Code is "speech_in_progress" or "speech_busy")
                    {await Task.Delay(1500,timeout.Token).ConfigureAwait(false);continue;}
                    throw failure;
                }
                if(response.Content.Headers.ContentType?.MediaType!="application/x-ndjson")throw new InvalidDataException(L.T("speech.invalidAudio"));
                using var stream=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var reader=new StreamReader(stream,Encoding.UTF8,detectEncodingFromByteOrderMarks:false,bufferSize:32768);
                while(await reader.ReadLineAsync().WaitAsync(timeout.Token).ConfigureAwait(false) is { } line)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if(line.Length>32768)throw new InvalidDataException(L.T("speech.invalidAudio"));
                    using var json=JsonDocument.Parse(line);
                    var root=json.RootElement;
                    switch(root.GetProperty("type").GetString())
                    {
                        case "audio":
                            if(root.GetProperty("sample_rate").GetInt32()!=24000||root.GetProperty("channels").GetInt32()!=1)throw new InvalidDataException(L.T("speech.invalidAudio"));
                            byte[] pcm=Convert.FromBase64String(root.GetProperty("pcm").GetString()??"");
                            bytes+=pcm.Length;
                            if(pcm.Length==0||pcm.Length%2!=0||bytes>MaximumAudioBytes)throw new InvalidDataException(L.T("speech.invalidAudio"));
                            cancellation.ThrowIfCancellationRequested();audio(pcm);break;
                        case "done":
                            if(bytes<480||root.GetProperty("pcm_bytes").GetInt32()!=bytes)throw new InvalidDataException(L.T("speech.invalidAudio"));
                            return;
                        case "error":
                            throw new SpeechRequestException(root.GetProperty("code").GetString()??"speech_unavailable",
                                L.IsChinese?root.GetProperty("message").GetString()??L.T("speech.failed"):L.T("speech.failed"));
                        default:throw new InvalidDataException(L.T("speech.invalidAudio"));
                    }
                }
                throw new IOException(L.T("speech.streamInterrupted"));
            }
            catch(HttpRequestException) when(bytes==0&&attempts++<2)
            {await Task.Delay(1200,timeout.Token).ConfigureAwait(false);}
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new SpeechRequestException("speech_login_required", L.T("speech.loginRequired"));
        var request = new HttpRequestMessage(method, origin.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private async Task<byte[]> AudioAsync(string token, string path, string? reply, CancellationToken cancellation)
    {
        using var language = L.BeginRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(280));
        int networkRetries = 0;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var request = Request(reply is null ? HttpMethod.Get : HttpMethod.Post,path,token);
                if (reply is not null) request.Content = new StringContent(JsonSerializer.Serialize(new { reply }),Encoding.UTF8,"application/json");
                using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failure = await Failure(response).ConfigureAwait(false);
                    if (reply is not null && failure.Code is "speech_in_progress" or "speech_busy")
                    { await Task.Delay(1500,timeout.Token).ConfigureAwait(false); continue; }
                    throw failure;
                }
                if (response.Content.Headers.ContentLength > MaximumAudioBytes)
                    throw new InvalidDataException(L.T("speech.invalidAudio"));
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                byte[] buffer = new byte[16384];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(),timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + read > MaximumAudioBytes) throw new InvalidDataException(L.T("speech.invalidAudio"));
                    output.Write(buffer,0,read);
                }
                byte[] audio = output.ToArray();
                if (audio.Length < 44 || Encoding.ASCII.GetString(audio,0,4) != "RIFF" || Encoding.ASCII.GetString(audio,8,4) != "WAVE")
                    throw new InvalidDataException(L.T("speech.invalidAudio"));
                return audio;
            }
            catch (HttpRequestException) when (reply is not null && networkRetries++ < 2)
            { await Task.Delay(1200,timeout.Token).ConfigureAwait(false); }
        }
    }
    private static async Task<SpeechRequestException> Failure(HttpResponseMessage response)
    {
        string code = response.StatusCode == HttpStatusCode.Unauthorized ? "speech_login_required" : "speech_unavailable";
        string message = response.StatusCode == HttpStatusCode.Unauthorized ? L.T("speech.loginRequired") : L.T("speech.failed");
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("error",out var error))
            {
                if (error.TryGetProperty("code",out var c)) code = c.GetString() ?? code;
                if (L.IsChinese && error.TryGetProperty("message",out var m)) message = m.GetString() ?? message;
            }
        }
        catch (JsonException) { }
        return new(code,message);
    }
}

public sealed class SpeechRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

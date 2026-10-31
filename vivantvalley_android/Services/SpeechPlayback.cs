using Microsoft.Xna.Framework.Audio;
using System.Collections.Concurrent;

namespace VivantValley.Services;

/// <summary>Audio objects are created and disposed on the game thread and belong to one menu.</summary>
public sealed class SpeechPlayback : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task<byte[]>? pending;
    private SoundEffect? effect;
    private SoundEffectInstance? playing;
    private object? owner;
    private Action<Exception>? onError;
    private Task? streamTask;
    private ConcurrentQueue<byte[]>? pcmQueue;
    private DynamicSoundEffectInstance? dynamicAudio;
    public bool IsBusy => pending is not null || streamTask is not null || dynamicAudio is not null || playing?.State == SoundState.Playing;

    public void StartStream(object menu,Func<Action<byte[]>,CancellationToken,Task> generate,Action<Exception>? error=null)
    {
        Stop();owner=menu;onError=error;cancellation=new();
        var queue=new ConcurrentQueue<byte[]>();pcmQueue=queue;
        try {streamTask=generate(queue.Enqueue,cancellation.Token);}
        catch(Exception ex){Stop();error?.Invoke(ex);}
    }

    public void Start(object menu, Func<CancellationToken,Task<byte[]>> generate, Action<Exception>? error = null)
    {
        Stop();
        owner = menu; onError = error;
        cancellation = new();
        try { pending = generate(cancellation.Token); }
        catch (Exception ex) { Stop(); error?.Invoke(ex); }
    }
    public void Update(object? currentMenu, float volume)
    {
        if (owner is null) return;
        if (!ReferenceEquals(owner,currentMenu)) { Stop(); return; }
        if (streamTask is { } streaming)
        {
            try
            {
                if (streaming.IsFaulted || streaming.IsCanceled) streaming.GetAwaiter().GetResult();
                if (pcmQueue is { IsEmpty:false })
                {
                    dynamicAudio ??= new DynamicSoundEffectInstance(24000,AudioChannels.Mono);
                    while(dynamicAudio.PendingBufferCount<8 && pcmQueue.TryDequeue(out var chunk)) dynamicAudio.SubmitBuffer(chunk);
                    dynamicAudio.Volume=Math.Clamp(volume,0f,1f);
                    if(dynamicAudio.State!=SoundState.Playing)dynamicAudio.Play();
                }
                if(dynamicAudio is not null)dynamicAudio.Volume=Math.Clamp(volume,0f,1f);
                if(streaming.IsCompleted && pcmQueue?.IsEmpty==true && (dynamicAudio is null||dynamicAudio.PendingBufferCount==0))Stop();
            }
            catch(Exception ex)
            {
                var report=onError;Stop();
                if(ex is not OperationCanceledException)report?.Invoke(ex);
            }
            return;
        }
        if (pending is { IsCompleted: true } task)
        {
            pending = null;
            try
            {
                using var stream = new MemoryStream(task.GetAwaiter().GetResult(),writable:false);
                effect = SoundEffect.FromStream(stream);
                playing = effect.CreateInstance();
                playing.Volume = Math.Clamp(volume,0f,1f);
                playing.Play();
            }
            catch (Exception ex)
            {
                var report = onError;
                Stop();
                if (ex is not OperationCanceledException) report?.Invoke(ex);
            }
        }
        if (playing is not null)
        {
            playing.Volume = Math.Clamp(volume,0f,1f);
            if (playing.State == SoundState.Stopped) Stop();
        }
    }
    public void Stop()
    {
        cancellation?.Cancel();
        if (pending is { } task) _ = task.ContinueWith(t => { _ = t.Exception; },TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        if(streamTask is { } streaming) _=streaming.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        streamTask=null;pcmQueue=null;
        dynamicAudio?.Stop();dynamicAudio?.Dispose();dynamicAudio=null;
        cancellation?.Dispose(); cancellation = null;
        playing?.Stop(); playing?.Dispose(); playing = null;
        effect?.Dispose(); effect = null;
        owner = null; onError = null;
    }
    public void Dispose() => Stop();
}

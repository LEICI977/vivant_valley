namespace VivantValley;

/// <summary>Optional anonymous hosted diagnostics. Never sent to a custom upstream.</summary>
public sealed class ConversationRequestContext
{
    public string? SpeechLanguage { get; init; }
    public string? TurnId { get; init; }
    public bool? VoiceEnabled { get; init; }
    public string? VoiceOptionId { get; init; }
    public string? SaveId { get; init; }
    public string? SessionId { get; init; }
    public string? Mode { get; init; }
    public long? ContextEpoch { get; init; }
    public string PromptVersion { get; init; } = "context-v2";

    public object ToHostedPayload(string? npcName, string? taskKind) => new
    {
        npc_name = string.IsNullOrWhiteSpace(npcName) ? null : npcName.Trim(),
        task = taskKind,
        save_id = SaveId,
        session_id = SessionId,
        mode = Mode,
        context_epoch = ContextEpoch,
        prompt_version = PromptVersion,
        turn_id = TurnId,
        speech_language = VoiceEnabled == true ? SpeechLanguage : null,
        voice_enabled = VoiceEnabled,
        speech_performance = VoiceEnabled == true,
        voice_option_id = VoiceOptionId,
    };
}

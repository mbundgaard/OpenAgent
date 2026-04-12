using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmVoice.GeminiLive.Models;

// ── Outgoing messages ────────────────────────────────────────────────────────

/// <summary>
/// Top-level outgoing message to the Gemini Live API. Only one field is populated per send.
/// </summary>
internal sealed class GeminiClientMessage
{
    [JsonPropertyName("setup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiSetup? Setup { get; set; }

    [JsonPropertyName("realtimeInput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiRealtimeInput? RealtimeInput { get; set; }

    [JsonPropertyName("toolResponse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiToolResponse? ToolResponse { get; set; }

    [JsonPropertyName("clientContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiClientContent? ClientContent { get; set; }
}

internal sealed class GeminiSetup
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("generation_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiGenerationConfig? GenerationConfig { get; set; }

    [JsonPropertyName("system_instruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiSystemInstruction? SystemInstruction { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GeminiToolSet>? Tools { get; set; }
}

internal sealed class GeminiGenerationConfig
{
    [JsonPropertyName("response_modalities")]
    public string[] ResponseModalities { get; set; } = ["AUDIO"];

    [JsonPropertyName("speech_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiSpeechConfig? SpeechConfig { get; set; }
}

internal sealed class GeminiSpeechConfig
{
    [JsonPropertyName("voice_config")]
    public GeminiVoiceConfig? VoiceConfig { get; set; }
}

internal sealed class GeminiVoiceConfig
{
    [JsonPropertyName("prebuilt_voice_config")]
    public GeminiPrebuiltVoiceConfig? PrebuiltVoiceConfig { get; set; }
}

internal sealed class GeminiPrebuiltVoiceConfig
{
    [JsonPropertyName("voice_name")]
    public required string VoiceName { get; set; }
}

internal sealed class GeminiSystemInstruction
{
    [JsonPropertyName("parts")]
    public required IReadOnlyList<GeminiTextPart> Parts { get; set; }
}

internal sealed class GeminiTextPart
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class GeminiToolSet
{
    [JsonPropertyName("functionDeclarations")]
    public required IReadOnlyList<GeminiFunctionDeclaration> FunctionDeclarations { get; set; }
}

internal sealed class GeminiFunctionDeclaration
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; set; }
}

internal sealed class GeminiRealtimeInput
{
    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiRealtimeAudio? Audio { get; set; }
}

internal sealed class GeminiRealtimeAudio
{
    [JsonPropertyName("realtimeInputAudio")]
    public required GeminiRealtimeInputAudio RealtimeInputAudio { get; set; }
}

internal sealed class GeminiRealtimeInputAudio
{
    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "audio/pcm;rate=16000";
}

internal sealed class GeminiToolResponse
{
    [JsonPropertyName("functionResponses")]
    public required IReadOnlyList<GeminiFunctionResponse> FunctionResponses { get; set; }
}

internal sealed class GeminiFunctionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("response")]
    public required GeminiFunctionResponseBody Response { get; set; }
}

internal sealed class GeminiFunctionResponseBody
{
    [JsonPropertyName("output")]
    public required object Output { get; set; }
}

internal sealed class GeminiClientContent
{
    [JsonPropertyName("turns")]
    public required IReadOnlyList<object> Turns { get; set; }

    [JsonPropertyName("turnComplete")]
    public bool TurnComplete { get; set; }
}

// ── Incoming messages ────────────────────────────────────────────────────────

/// <summary>
/// Top-level incoming message from the Gemini Live API.
/// Gemini has no top-level type discriminator — inspect which property is non-null.
/// </summary>
internal sealed class GeminiServerMessage
{
    [JsonPropertyName("setupComplete")]
    public JsonElement? SetupComplete { get; set; }

    [JsonPropertyName("serverContent")]
    public GeminiServerContent? ServerContent { get; set; }

    [JsonPropertyName("toolCall")]
    public GeminiToolCall? ToolCall { get; set; }

    [JsonPropertyName("goAway")]
    public JsonElement? GoAway { get; set; }

    [JsonPropertyName("error")]
    public GeminiError? Error { get; set; }
}

internal sealed class GeminiServerContent
{
    [JsonPropertyName("modelTurn")]
    public GeminiModelTurn? ModelTurn { get; set; }

    [JsonPropertyName("turnComplete")]
    public bool? TurnComplete { get; set; }

    [JsonPropertyName("interrupted")]
    public bool? Interrupted { get; set; }

    [JsonPropertyName("outputTranscription")]
    public GeminiTranscription? OutputTranscription { get; set; }

    [JsonPropertyName("inputTranscription")]
    public GeminiTranscription? InputTranscription { get; set; }
}

internal sealed class GeminiModelTurn
{
    [JsonPropertyName("parts")]
    public List<GeminiPart>? Parts { get; set; }
}

internal sealed class GeminiPart
{
    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

internal sealed class GeminiTranscription
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class GeminiToolCall
{
    [JsonPropertyName("functionCalls")]
    public List<GeminiFunctionCall>? FunctionCalls { get; set; }
}

internal sealed class GeminiFunctionCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Tool arguments as a JSON object (not a string — Gemini sends structured args).</summary>
    [JsonPropertyName("args")]
    public JsonElement Args { get; set; }
}

internal sealed class GeminiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

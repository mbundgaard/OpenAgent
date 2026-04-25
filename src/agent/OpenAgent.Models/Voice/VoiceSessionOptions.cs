namespace OpenAgent.Models.Voice;

/// <summary>
/// Per-session audio configuration. Channels override the provider's default codec and sample rate.
/// </summary>
/// <param name="Codec">Codec identifier as understood by the provider (pcm16, g711_ulaw, g711_alaw).</param>
/// <param name="SampleRate">Sample rate in Hz (8000 for g711_*, 24000 for pcm16 on Azure/Grok).</param>
public sealed record VoiceSessionOptions(string Codec, int SampleRate);

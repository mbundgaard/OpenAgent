using System.Reflection;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Static accessor for the embedded thinking-sound clip — 8 kHz mono A-law, ready to stream
/// directly as Telnyx Media Streaming payload bytes (160 bytes = 20ms at 8 kHz). Loaded once
/// on first access and cached for the lifetime of the process.
/// </summary>
internal static class ThinkingClip
{
    private const string ResourceName = "OpenAgent.Channel.Telnyx.thinking-clip.al";
    private static readonly Lazy<byte[]> _bytes = new(LoadBytes, isThreadSafe: true);

    /// <summary>Raw A-law payload, byte-aligned to the codec (1 byte = 1 sample at 8 kHz).</summary>
    public static byte[] Bytes => _bytes.Value;

    private static byte[] LoadBytes()
    {
        var asm = typeof(ThinkingClip).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {asm.GetName().Name}.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

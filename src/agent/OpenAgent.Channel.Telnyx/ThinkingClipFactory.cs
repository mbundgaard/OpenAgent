// OpenAgent.Channel.Telnyx/ThinkingClipFactory.cs
namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Generates the default seamless-loop thinking clip used by <see cref="TelnyxMediaBridge"/>'s
/// thinking pump. The clip is band-limited soft pink noise (300-1000 Hz proxy via summed sines)
/// encoded as 8 kHz µ-law mono with a ~50 ms cosine fade across the loop boundary so repeats
/// are click-free. No third-party audio asset, no licensing concern.
/// </summary>
public static class ThinkingClipFactory
{
    private const int SampleRate = 8000;
    private const double Duration = 2.0;
    private const double FadeSeconds = 0.05;

    public static byte[] Generate()
    {
        var sampleCount = (int)(SampleRate * Duration);
        var fadeSamples = (int)(SampleRate * FadeSeconds);
        var pcm = new short[sampleCount];

        // A few overlapping low-frequency sines simulate soft ambient noise without true RNG —
        // deterministic output makes tests reproducible.
        var freqs = new[] { 320.0, 470.0, 610.0, 880.0 };
        var phases = new[] { 0.0, 0.7, 1.4, 2.1 };
        const double amp = 1500; // ~ -28 dBFS

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRate;
            double s = 0;
            for (var k = 0; k < freqs.Length; k++)
                s += Math.Sin(2 * Math.PI * freqs[k] * t + phases[k]);
            pcm[i] = (short)(s * amp / freqs.Length);
        }

        // Cosine fade-in at the head AND fade-out at the tail. Because head fades up from 0 and
        // tail fades down to 0, the loop boundary (tail->head) is silent on both sides — click-free.
        for (var i = 0; i < fadeSamples; i++)
        {
            var w = (1 - Math.Cos(Math.PI * i / fadeSamples)) / 2;
            pcm[i] = (short)(pcm[i] * w);
            pcm[sampleCount - 1 - i] = (short)(pcm[sampleCount - 1 - i] * w);
        }

        var ulaw = new byte[sampleCount];
        for (var i = 0; i < sampleCount; i++) ulaw[i] = LinearToUlaw(pcm[i]);
        return ulaw;
    }

    // Standard ITU-T G.711 µ-law encoding.
    private static byte LinearToUlaw(short pcm)
    {
        const int BIAS = 0x84;
        const int CLIP = 32635;
        var sign = (pcm >> 8) & 0x80;
        if (sign != 0) pcm = (short)-pcm;
        if (pcm > CLIP) pcm = CLIP;
        pcm += BIAS;
        var exponent = 7;
        for (var mask = 0x4000; (pcm & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        var mantissa = (pcm >> (exponent + 3)) & 0x0F;
        var ulaw = ~(sign | (exponent << 4) | mantissa) & 0xFF;
        return (byte)ulaw;
    }
}

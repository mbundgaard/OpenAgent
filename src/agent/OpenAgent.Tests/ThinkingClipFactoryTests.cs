// OpenAgent.Tests/ThinkingClipFactoryTests.cs
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class ThinkingClipFactoryTests
{
    [Fact]
    public void Generate_ReturnsExpectedFrameCount_ForTwoSeconds()
    {
        var clip = ThinkingClipFactory.Generate();
        // 2 seconds @ 8 kHz = 16000 samples = 16000 µ-law bytes (one byte per sample)
        Assert.Equal(16000, clip.Length);
    }

    [Fact]
    public void Generate_LoopBoundary_HasCosineFade_NoClicks()
    {
        var clip = ThinkingClipFactory.Generate();
        // Last 50 ms (400 samples) and first 50 ms should both be near silence (µ-law silence = 0xFF or 0x7F).
        // We assert the absolute amplitude near both edges is below the clip-mean amplitude — proxy for fade.
        var mean = MeanAmplitude(clip, 400, clip.Length - 400);
        var headEdge = MeanAmplitude(clip, 0, 50);
        var tailEdge = MeanAmplitude(clip, clip.Length - 50, clip.Length);
        Assert.True(headEdge < mean * 0.75, $"head edge {headEdge} should be quieter than mean {mean}");
        Assert.True(tailEdge < mean * 0.75, $"tail edge {tailEdge} should be quieter than mean {mean}");
    }

    private static double MeanAmplitude(byte[] ulaw, int start, int end)
    {
        // µ-law decode is not necessary for a relative comparison. The encoder inverts the
        // sign+exponent+mantissa bits in its final step, so silence (pcm=0) maps to bytes near
        // 0xFF (positive zero) or 0x7F (negative zero), and full-scale maps to bytes near 0x80
        // / 0x00. The 7-bit magnitude after inverting (~b & 0x7F) is therefore a monotonic
        // proxy for amplitude — 0 at silence, 0x7F at full-scale.
        double sum = 0;
        for (var i = start; i < end; i++) sum += ~ulaw[i] & 0x7F;
        return sum / (end - start);
    }
}

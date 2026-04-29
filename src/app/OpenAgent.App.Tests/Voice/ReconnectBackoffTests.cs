using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class ReconnectBackoffTests
{
    [Fact]
    public void Schedule_is_1_2_4_8s_then_gives_up()
    {
        var b = new ReconnectBackoff(maxTries: 5);
        Assert.Equal(TimeSpan.FromSeconds(1), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), b.NextDelay()); // capped
        Assert.True(b.GiveUp);
    }

    [Fact]
    public void Reset_resets_attempt_count()
    {
        var b = new ReconnectBackoff(maxTries: 3);
        b.NextDelay(); b.NextDelay(); b.NextDelay();
        Assert.True(b.GiveUp);
        b.Reset();
        Assert.False(b.GiveUp);
    }
}

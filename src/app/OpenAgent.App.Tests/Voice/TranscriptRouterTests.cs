using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class TranscriptRouterTests
{
    private readonly List<(string kind, string content)> _events = new();
    private TranscriptRouter Make() => new(
        onAppend: (src, text) => _events.Add(("APPEND_" + src, text)),
        onUpdateLast: (text) => _events.Add(("UPDATE", text))
    );

    [Fact]
    public void First_delta_appends()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        Assert.Single(_events);
        Assert.Equal("APPEND_User", _events[0].kind);
        Assert.Equal("hi", _events[0].content);
    }

    [Fact]
    public void Same_source_grows_via_update()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDelta(TranscriptSource.User, " there");
        Assert.Equal(2, _events.Count);
        Assert.Equal("UPDATE", _events[1].kind);
        Assert.Equal("hi there", _events[1].content);
    }

    [Fact]
    public void Source_flip_appends_new_bubble()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDelta(TranscriptSource.Assistant, "yo");
        Assert.Equal(2, _events.Count);
        Assert.Equal("APPEND_Assistant", _events[1].kind);
    }

    [Fact]
    public void Done_resets_so_next_delta_appends()
    {
        var r = Make();
        r.OnDelta(TranscriptSource.User, "hi");
        r.OnDone();
        r.OnDelta(TranscriptSource.User, "again");
        Assert.Equal(2, _events.Count);
        Assert.Equal("APPEND_User", _events[1].kind);
    }
}

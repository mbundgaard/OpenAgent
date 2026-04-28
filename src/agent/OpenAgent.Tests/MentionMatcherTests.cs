using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class MentionMatcherTests
{
    private static Conversation Conv(List<string>? mentionFilter) => new()
    {
        Id = "c1",
        Source = "test",
        TextProvider = "p", VoiceProvider = "p",
        TextModel = "m", VoiceModel = "m",
        MentionFilter = mentionFilter
    };

    [Fact]
    public void ShouldAccept_NullMentionFilter_AcceptsAnyText()
    {
        Assert.True(MentionMatcher.ShouldAccept(Conv(null), "anything"));
        Assert.True(MentionMatcher.ShouldAccept(Conv(null), ""));
    }

    [Fact]
    public void ShouldAccept_EmptyMentionFilter_AcceptsAnyText()
    {
        Assert.True(MentionMatcher.ShouldAccept(Conv([]), "anything"));
    }

    [Fact]
    public void ShouldAccept_TextContainsName_CaseInsensitive_Accepts()
    {
        var conv = Conv(["Dex"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey Dex"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey DEX!"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey dex"));
    }

    [Fact]
    public void ShouldAccept_TextMissingName_Rejects()
    {
        var conv = Conv(["Dex"]);
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello world"));
        Assert.False(MentionMatcher.ShouldAccept(conv, ""));
    }

    [Fact]
    public void ShouldAccept_SubstringMatch_MatchesInsideOtherWords()
    {
        // Documented v1 semantics: substring match, not word-boundary.
        var conv = Conv(["Dex"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "look at the index"));
    }

    [Fact]
    public void ShouldAccept_MultipleNames_AnyMatchAccepts()
    {
        var conv = Conv(["Dex", "fox"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "hello fox"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hello DEX"));
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello cat"));
    }

    [Fact]
    public void ShouldAccept_EmptyOrWhitespaceNames_AreIgnored()
    {
        // A lone empty string must not match everything.
        var conv = Conv(["", "Dex"]);
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "dex"));
    }
}

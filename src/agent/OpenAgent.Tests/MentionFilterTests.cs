using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class MentionFilterTests
{
    private static Conversation Conv(List<string>? mentionNames) => new()
    {
        Id = "c1",
        Source = "test",
        Type = ConversationType.Text,
        Provider = "p",
        Model = "m",
        MentionNames = mentionNames
    };

    [Fact]
    public void ShouldAccept_NullMentionNames_AcceptsAnyText()
    {
        Assert.True(MentionFilter.ShouldAccept(Conv(null), "anything"));
        Assert.True(MentionFilter.ShouldAccept(Conv(null), ""));
    }

    [Fact]
    public void ShouldAccept_EmptyMentionNames_AcceptsAnyText()
    {
        Assert.True(MentionFilter.ShouldAccept(Conv([]), "anything"));
    }

    [Fact]
    public void ShouldAccept_TextContainsName_CaseInsensitive_Accepts()
    {
        var conv = Conv(["Dex"]);
        Assert.True(MentionFilter.ShouldAccept(conv, "hey Dex"));
        Assert.True(MentionFilter.ShouldAccept(conv, "hey DEX!"));
        Assert.True(MentionFilter.ShouldAccept(conv, "hey dex"));
    }

    [Fact]
    public void ShouldAccept_TextMissingName_Rejects()
    {
        var conv = Conv(["Dex"]);
        Assert.False(MentionFilter.ShouldAccept(conv, "hello world"));
        Assert.False(MentionFilter.ShouldAccept(conv, ""));
    }

    [Fact]
    public void ShouldAccept_SubstringMatch_MatchesInsideOtherWords()
    {
        // Documented v1 semantics: substring match, not word-boundary.
        var conv = Conv(["Dex"]);
        Assert.True(MentionFilter.ShouldAccept(conv, "look at the index"));
    }

    [Fact]
    public void ShouldAccept_MultipleNames_AnyMatchAccepts()
    {
        var conv = Conv(["Dex", "fox"]);
        Assert.True(MentionFilter.ShouldAccept(conv, "hello fox"));
        Assert.True(MentionFilter.ShouldAccept(conv, "hello DEX"));
        Assert.False(MentionFilter.ShouldAccept(conv, "hello cat"));
    }

    [Fact]
    public void ShouldAccept_EmptyOrWhitespaceNames_AreIgnored()
    {
        // A lone empty string must not match everything.
        var conv = Conv(["", "Dex"]);
        Assert.False(MentionFilter.ShouldAccept(conv, "hello"));
        Assert.True(MentionFilter.ShouldAccept(conv, "dex"));
    }
}

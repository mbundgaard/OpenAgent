using OpenAgent.Channel.Telegram;

namespace OpenAgent.Tests;

public class TelegramAccessControlTests
{
    [Fact]
    public void EmptyAllowList_BlocksAll()
    {
        var acl = new TelegramAccessControl([]);
        Assert.False(acl.IsAllowed(123456789));
    }

    [Fact]
    public void AllowedUserId_IsAllowed()
    {
        var acl = new TelegramAccessControl([123456789, 987654321]);
        Assert.True(acl.IsAllowed(123456789));
        Assert.True(acl.IsAllowed(987654321));
    }

    [Fact]
    public void UnknownUserId_IsBlocked()
    {
        var acl = new TelegramAccessControl([123456789]);
        Assert.False(acl.IsAllowed(999999999));
    }
}

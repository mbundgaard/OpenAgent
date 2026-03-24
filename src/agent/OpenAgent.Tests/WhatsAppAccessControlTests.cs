using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppAccessControlTests
{
    [Fact]
    public void IsAllowed_EmptyList_AllowsEveryone()
    {
        var ac = new WhatsAppAccessControl([]);
        Assert.True(ac.IsAllowed("+4512345678@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_MatchingJid_ReturnsTrue()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net"]);
        Assert.True(ac.IsAllowed("+4512345678@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_NonMatchingJid_ReturnsFalse()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net"]);
        Assert.False(ac.IsAllowed("+4599999999@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_GroupJid_ReturnsTrue()
    {
        var ac = new WhatsAppAccessControl(["120363001234567890@g.us"]);
        Assert.True(ac.IsAllowed("120363001234567890@g.us"));
    }

    [Fact]
    public void IsAllowed_MixedList_MatchesBoth()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net", "120363001234567890@g.us"]);
        Assert.True(ac.IsAllowed("+4512345678@s.whatsapp.net"));
        Assert.True(ac.IsAllowed("120363001234567890@g.us"));
        Assert.False(ac.IsAllowed("+4599999999@s.whatsapp.net"));
    }
}

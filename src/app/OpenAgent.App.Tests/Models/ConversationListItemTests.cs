using System.Text.Json;
using OpenAgent.App.Core.Models;

namespace OpenAgent.App.Tests.Models;

public class ConversationListItemTests
{
    [Fact]
    public void Round_trip_against_canned_fixture()
    {
        var json = File.ReadAllText("Fixtures/conversation-list.json");
        var items = JsonSerializer.Deserialize<List<ConversationListItem>>(json, JsonOptions.Default);
        Assert.NotNull(items);
        Assert.Equal(2, items!.Count);
        Assert.Equal("abc-123", items[0].Id);
        Assert.Equal("telegram", items[0].Source);
        Assert.Equal("Pricing chat", items[0].DisplayName);
        Assert.Equal("Discuss pricing tiers", items[0].Intention);
        Assert.Equal(14, items[0].TurnCount);
        Assert.Equal(new DateTimeOffset(2026, 4, 29, 8, 30, 0, TimeSpan.Zero), items[0].LastActivity);
        Assert.Null(items[1].DisplayName);
        Assert.Null(items[1].Intention);
        Assert.Null(items[1].LastActivity);
    }

    [Fact]
    public void Title_helper_falls_back_through_display_name_intention_id()
    {
        var item = new ConversationListItem { Id = "id-1", Source = "app", CreatedAt = DateTimeOffset.UtcNow };
        Assert.Equal("id-1", item.Title);

        var withIntention = item with { Intention = "I" };
        Assert.Equal("I", withIntention.Title);

        var withDisplay = withIntention with { DisplayName = "D" };
        Assert.Equal("D", withDisplay.Title);
    }
}

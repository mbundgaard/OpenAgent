using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Models.Connections;

/// <summary>
/// A configured link between an external channel (Telegram bot, WhatsApp number, etc.)
/// and an OpenAgent conversation.
/// </summary>
public sealed class Connection
{
    /// <summary>Unique identifier for this connection.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Display name shown in the UI.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Channel type: "telegram", "whatsapp", "telnyx".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>Whether this connection should be started automatically.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether new conversations can be created from incoming messages.
    /// When false, only existing conversations receive responses.
    /// Auto-locks to false after the first conversation is created.
    /// </summary>
    [JsonPropertyName("allowNewConversations")]
    public bool AllowNewConversations { get; set; } = true;

    /// <summary>The conversation this connection feeds into.</summary>
    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; set; }

    /// <summary>Type-specific configuration blob, interpreted by the channel provider.</summary>
    [JsonPropertyName("config")]
    public JsonElement Config { get; set; }
}

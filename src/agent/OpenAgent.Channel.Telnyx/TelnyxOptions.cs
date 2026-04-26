namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Strongly-typed configuration for a Telnyx Media Streaming connection. Populated by
/// <see cref="TelnyxChannelProviderFactory.Create"/> from the connection's JsonElement config blob.
/// </summary>
public sealed class TelnyxOptions
{
    /// <summary>Telnyx API key (v2). Used as Authorization: Bearer for Call Control REST commands.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The E.164 phone number this connection owns (e.g. "+4535150636"). Cosmetic; routing is on Telnyx side.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Telnyx connection ID of the Call Control connection routing the number. Used to validate webhook payloads.</summary>
    public string? CallControlAppId { get; set; }

    /// <summary>Public HTTPS URL of this OpenAgent instance. Webhook + WebSocket URLs derive from it.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>PEM-encoded ED25519 public key from the Telnyx Developer Hub. When blank, signature verification is SKIPPED with a warning (dev only).</summary>
    public string? WebhookPublicKey { get; set; }

    /// <summary>E.164 numbers allowed to call. Empty list = allow all.</summary>
    public List<string> AllowedNumbers { get; set; } = [];

    /// <summary>Auto-generated 12-hex GUID identifying this connection's webhook URLs. Populated on first start; persisted to connections.json.</summary>
    public string? WebhookId { get; set; }
}

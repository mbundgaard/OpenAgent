namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Strongly-typed configuration for a Telnyx channel connection.
/// Populated by <see cref="TelnyxChannelProviderFactory.Create"/> from the
/// connection's JsonElement config blob.
/// </summary>
public sealed class TelnyxOptions
{
    /// <summary>Telnyx API key (v2 key from the portal).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The E.164 phone number this connection owns (e.g. "+4512345678").</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>HMAC secret used to verify inbound webhook signatures.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// E.164 numbers allowed to call the agent. Empty list means allow all — caller
    /// restriction is enforced in plan 2 when inbound webhooks land.
    /// </summary>
    // TODO(plan-2): validate E.164 format for PhoneNumber and AllowedNumbers in the factory.
    public List<string> AllowedNumbers { get; set; } = [];

    /// <summary>
    /// Auto-generated GUID identifying this connection's webhook endpoint.
    /// Populated on first start if absent; persisted in the connection config.
    /// Used in the webhook URL: /api/webhook/telnyx/{WebhookId}/voice.
    /// </summary>
    public string? WebhookId { get; set; }

    /// <summary>
    /// PEM-encoded ED25519 public key from the Telnyx portal. Used to verify
    /// webhook signatures. When null, signatures are NOT verified — accept only
    /// for local development.
    /// </summary>
    public string? WebhookPublicKey { get; set; }

    /// <summary>
    /// Public base URL of this OpenAgent instance (e.g. "https://openagent.example.com").
    /// Used to build the `action` URL in TeXML Gather verbs so Telnyx knows where
    /// to post the next turn's speech result. Required for Telnyx callbacks to work.
    /// </summary>
    public string? BaseUrl { get; set; }
}

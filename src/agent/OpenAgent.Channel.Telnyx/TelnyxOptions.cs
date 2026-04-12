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
}

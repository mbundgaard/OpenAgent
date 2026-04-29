namespace OpenAgent.App.Core.Onboarding;

/// <summary>Parsed credentials from a QR code or manual entry.</summary>
public sealed record QrPayload(string BaseUrl, string Token);

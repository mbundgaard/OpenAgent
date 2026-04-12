using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Verifies Telnyx webhook signatures.
///
/// Payload: UTF-8 bytes of <c>{timestamp}|{raw-body}</c>.
/// Header "Telnyx-Signature-ed25519" carries base64-encoded ED25519 signature.
/// Header "Telnyx-Timestamp" carries unix seconds; anti-replay window is 300s.
/// </summary>
public sealed class TelnyxSignatureVerifier
{
    private const int MaxClockSkewSeconds = 300;

    private readonly ILogger<TelnyxSignatureVerifier> _logger;

    /// <summary>Initializes the verifier with a logger.</summary>
    public TelnyxSignatureVerifier(ILogger<TelnyxSignatureVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verify a signed request.
    /// When publicKeyPem is null or empty, verification is SKIPPED and a warning
    /// is logged — used only in local development.
    /// </summary>
    public bool Verify(
        string? publicKeyPem,
        string? signatureHeader,
        string? timestampHeader,
        byte[] rawBody,
        DateTimeOffset now)
    {
        // Dev mode: no key configured — skip verification with warning
        if (string.IsNullOrEmpty(publicKeyPem))
        {
            _logger.LogWarning("Telnyx signature verification skipped — no public key configured");
            return true;
        }

        // Whitespace-only key is almost certainly a misconfiguration (padded env var, form trim failure)
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            _logger.LogWarning("Telnyx webhook public key is whitespace-only — treating as misconfiguration");
            return false;
        }

        // Both headers must be present
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(timestampHeader))
        {
            _logger.LogWarning("Telnyx webhook missing signature or timestamp header");
            return false;
        }

        // Timestamp must be a valid integer
        if (!long.TryParse(timestampHeader, out var timestamp))
        {
            _logger.LogWarning("Telnyx webhook timestamp is not a valid integer");
            return false;
        }

        // Anti-replay: reject if outside clock-skew window
        var delta = Math.Abs(now.ToUnixTimeSeconds() - timestamp);
        if (delta > MaxClockSkewSeconds)
        {
            _logger.LogWarning("Telnyx webhook timestamp outside clock-skew window ({Delta}s)", delta);
            return false;
        }

        // Signature must be valid base64
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureHeader);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Telnyx webhook signature header is not valid base64");
            return false;
        }

        // Build signed payload: UTF8(timestamp) + '|' + rawBody
        var payload = new byte[Encoding.UTF8.GetByteCount(timestampHeader) + 1 + rawBody.Length];
        var written = Encoding.UTF8.GetBytes(timestampHeader, payload);
        payload[written] = (byte)'|';
        Buffer.BlockCopy(rawBody, 0, payload, written + 1, rawBody.Length);

        // Parse public key — distinct catch so key-parsing failures log separately from verify failures
        Ed25519PublicKeyParameters pubKey;
        try
        {
            using var sr = new StringReader(publicKeyPem);
            pubKey = (Ed25519PublicKeyParameters)new PemReader(sr).ReadObject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telnyx webhook public key could not be parsed as Ed25519");
            return false;
        }

        // Verify ED25519 signature using BouncyCastle
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(forSigning: false, pubKey);
            signer.BlockUpdate(payload, 0, payload.Length);
            return signer.VerifySignature(signatureBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telnyx webhook signature verification threw; treating as invalid");
            return false;
        }
    }
}

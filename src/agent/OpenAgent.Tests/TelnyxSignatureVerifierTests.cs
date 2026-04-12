using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace OpenAgent.Tests;

public class TelnyxSignatureVerifierTests
{
    private readonly TelnyxSignatureVerifier _verifier = new(NullLogger<TelnyxSignatureVerifier>.Instance);

    [Fact]
    public void Verify_returns_true_when_public_key_is_null()
    {
        var ok = _verifier.Verify(
            publicKeyPem: null,
            signatureHeader: "anything",
            timestampHeader: "1234567890",
            rawBody: Encoding.UTF8.GetBytes("{}"),
            now: DateTimeOffset.UnixEpoch);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_returns_true_for_valid_signature()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("""{"event_type":"call.initiated"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, timestamp, body);

        var ok = _verifier.Verify(publicPem, signature, timestamp, body, now);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_rejects_tampered_body()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("""{"event_type":"call.initiated"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, timestamp, body);

        var tampered = Encoding.UTF8.GetBytes("""{"event_type":"call.HACKED"}""");
        var ok = _verifier.Verify(publicPem, signature, timestamp, tampered, now);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_rejects_stale_timestamp()
    {
        var (publicPem, privatePem) = GenerateKeyPair();
        var body = Encoding.UTF8.GetBytes("{}");
        var stale = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var now = stale.AddMinutes(10);
        var staleTimestamp = stale.ToUnixTimeSeconds().ToString();
        var signature = SignForTest(privatePem, staleTimestamp, body);

        var ok = _verifier.Verify(publicPem, signature, staleTimestamp, body, now);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_rejects_missing_headers()
    {
        Assert.False(_verifier.Verify("-----BEGIN PUBLIC KEY-----\nX\n-----END PUBLIC KEY-----", null, "123", [], DateTimeOffset.Now));
        Assert.False(_verifier.Verify("-----BEGIN PUBLIC KEY-----\nX\n-----END PUBLIC KEY-----", "sig", null, [], DateTimeOffset.Now));
    }

    // --- Helpers ---

    /// <summary>
    /// Generate an Ed25519 key pair using BouncyCastle — same library the verifier uses.
    /// Returns (publicPem, privatePem).
    /// </summary>
    private static (string publicPem, string privatePem) GenerateKeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)keyPair.Private;
        var publicKey = (Ed25519PublicKeyParameters)keyPair.Public;

        string pubPem, privPem;
        using (var sw = new StringWriter())
        {
            var pemWriter = new PemWriter(sw);
            pemWriter.WriteObject(publicKey);
            pemWriter.Writer.Flush();
            pubPem = sw.ToString();
        }
        using (var sw = new StringWriter())
        {
            var pemWriter = new PemWriter(sw);
            pemWriter.WriteObject(privateKey);
            pemWriter.Writer.Flush();
            privPem = sw.ToString();
        }

        return (pubPem, privPem);
    }

    /// <summary>
    /// Sign a test payload (timestamp|body) using BouncyCastle and return base64 signature.
    /// </summary>
    private static string SignForTest(string privatePem, string timestamp, byte[] body)
    {
        var payload = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + body.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, payload);
        payload[written] = (byte)'|';
        Buffer.BlockCopy(body, 0, payload, written + 1, body.Length);

        Ed25519PrivateKeyParameters privateKey;
        using (var sr = new StringReader(privatePem))
        {
            var pemReader = new PemReader(sr);
            privateKey = (Ed25519PrivateKeyParameters)pemReader.ReadObject();
        }

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, privateKey);
        signer.BlockUpdate(payload, 0, payload.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }
}

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxSignatureVerifierTests
{
    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var body = "test=body"u8.ToArray();
        var signature = SignTimestampPlusBody(privateKey, ts, body);
        var sigB64 = Convert.ToBase64String(signature);

        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(publicPem, sigB64, ts, body, DateTimeOffset.FromUnixTimeSeconds(1700000000));

        Assert.True(ok);
    }

    [Fact]
    public void Verify_NoPublicKey_LogsWarning_ReturnsTrue()
    {
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(null, "sig", "ts", "body"u8.ToArray(), DateTimeOffset.UtcNow);
        Assert.True(ok);
    }

    [Fact]
    public void Verify_ExpiredTimestamp_ReturnsFalse()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var body = "x"u8.ToArray();
        var sig = Convert.ToBase64String(SignTimestampPlusBody(privateKey, ts, body));

        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        var ok = verifier.Verify(publicPem, sig, ts, body, DateTimeOffset.FromUnixTimeSeconds(1700001000)); // 1000s skew
        Assert.False(ok);
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var (publicPem, privateKey) = GenerateEd25519KeyPair();
        var ts = "1700000000";
        var sig = Convert.ToBase64String(SignTimestampPlusBody(privateKey, ts, "good"u8.ToArray()));
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        Assert.False(verifier.Verify(publicPem, sig, ts, "tampered"u8.ToArray(), DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    [Fact]
    public void Verify_MalformedKey_ReturnsFalse()
    {
        var verifier = new TelnyxSignatureVerifier(NullLogger<TelnyxSignatureVerifier>.Instance);
        Assert.False(verifier.Verify("not-a-pem", "sig", "1700000000", "x"u8.ToArray(), DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    private static (string PublicPem, Ed25519PrivateKeyParameters Private) GenerateEd25519KeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var pub = (Ed25519PublicKeyParameters)pair.Public;
        var priv = (Ed25519PrivateKeyParameters)pair.Private;

        using var sw = new StringWriter();
        var pem = new PemWriter(sw);
        pem.WriteObject(pub);
        pem.Writer.Flush();
        return (sw.ToString(), priv);
    }

    private static byte[] SignTimestampPlusBody(Ed25519PrivateKeyParameters key, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + "|");
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        return signer.GenerateSignature();
    }
}

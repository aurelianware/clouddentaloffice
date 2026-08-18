using System.Security.Cryptography;
using System.Text;

public sealed class ZocdocWebhookSignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("webhook-test-secret");
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"event_type\":\"appointment_updated\"}");

    [Fact]
    public void AcceptsDocumentedHmacOverTimestampDotRawBody()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(Body).ToArray();
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Secret, signed));

        Assert.True(ZocdocWebhookSignatureVerifier.Verify(Body, timestamp, $"v1:{signature}",
            Convert.ToBase64String(Secret), Now));
    }

    [Fact]
    public void RejectsInvalidSignatureAndStaleTimestamp()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        Assert.False(ZocdocWebhookSignatureVerifier.Verify(Body, timestamp, "v1:YWJj",
            Convert.ToBase64String(Secret), Now));
        Assert.False(ZocdocWebhookSignatureVerifier.Verify(Body,
            Now.AddMinutes(-6).ToUnixTimeSeconds().ToString(), "v1:YWJj",
            Convert.ToBase64String(Secret), Now));
    }

    [Theory]
    [InlineData("not-a-time", "v1:YWJj", "c2VjcmV0")]
    [InlineData("1800000000", "missing-version", "c2VjcmV0")]
    [InlineData("1800000000", "v1:YWJj", "not-base64")]
    public void RejectsMalformedHeadersOrSecret(string timestamp, string signature, string secret) =>
        Assert.False(ZocdocWebhookSignatureVerifier.Verify(Body, timestamp, signature, secret, Now));
}

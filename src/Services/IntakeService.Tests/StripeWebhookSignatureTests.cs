using System.Security.Cryptography;
using System.Text;

public sealed class StripeWebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"id\":\"evt_test\"}");
    private const string Secret = "whsec_test_only";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_787_180_000);

    [Fact]
    public void Valid_signature_over_raw_body_is_accepted()
    {
        var header = Header(Body, Now.ToUnixTimeSeconds());
        Assert.True(StripeWebhookSignatureVerifier.Verify(Body, header, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Modified_body_invalid_signature_and_stale_timestamp_are_rejected()
    {
        var header = Header(Body, Now.ToUnixTimeSeconds());
        Assert.False(StripeWebhookSignatureVerifier.Verify(Encoding.UTF8.GetBytes("{\"id\":\"changed\"}"),
            header, Secret, Now, TimeSpan.FromMinutes(5)));
        Assert.False(StripeWebhookSignatureVerifier.Verify(Body, "t=1,v1=invalid", Secret, Now,
            TimeSpan.FromMinutes(5)));
        var stale = Now.AddMinutes(-6);
        Assert.False(StripeWebhookSignatureVerifier.Verify(Body, Header(Body, stale.ToUnixTimeSeconds()), Secret,
            Now, TimeSpan.FromMinutes(5)));
    }

    private static string Header(byte[] body, long timestamp)
    {
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}");
        return $"t={timestamp},v1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), payload)).ToLowerInvariant()}";
    }
}

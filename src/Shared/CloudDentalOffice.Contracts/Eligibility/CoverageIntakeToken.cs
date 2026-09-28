using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudDentalOffice.Contracts.Eligibility;

/// <summary>What a coverage intake link grants: answering one request, for one practice, until it expires.</summary>
public sealed record CoverageIntakeTicket(string TenantId, Guid RequestId, string PracticeName, DateTimeOffset ExpiresAt);

/// <summary>
/// The signed token in a patient's coverage intake link. The portal issues it and
/// the public IntakeService checks it before showing the form, so the link itself
/// is the credential (new patients have no portal login). It is signed, not
/// encrypted: it carries only the practice's tenant slug and name, a request ID
/// and an expiry, never patient data. Both services share the signing key;
/// single use is enforced by the portal, which owns the request.
/// </summary>
public static class CoverageIntakeToken
{
    public const int MinimumKeyBytes = 32;

    // Domain separation, so a signature made for anything else never verifies here.
    private const string Purpose = "cdo-coverage-intake.v1.";

    public static string Create(CoverageIntakeTicket ticket, string signingKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(
            1, ticket.TenantId, ticket.RequestId, ticket.PracticeName, ticket.ExpiresAt.ToUnixTimeSeconds()));
        var encoded = Base64Url(payload);
        return $"{encoded}.{Base64Url(Sign(encoded, Key(signingKey)))}";
    }

    /// <summary>The ticket, if the token is well formed, correctly signed and not expired.</summary>
    public static bool TryRead(string? token, string signingKey, DateTimeOffset now, out CoverageIntakeTicket ticket)
    {
        ticket = null!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] signature, payloadBytes;
        try
        {
            signature = FromBase64Url(parts[1]);
            payloadBytes = FromBase64Url(parts[0]);
        }
        catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0], Key(signingKey)))) return false;

        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(payloadBytes); }
        catch (JsonException) { return false; }
        if (payload is not { V: 1 } || string.IsNullOrWhiteSpace(payload.T) || payload.R == Guid.Empty) return false;

        DateTimeOffset expires;
        try { expires = DateTimeOffset.FromUnixTimeSeconds(payload.E); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (expires <= now) return false;

        ticket = new(payload.T, payload.R, payload.P ?? string.Empty, expires);
        return true;
    }

    public static bool IsUsableKey(string? signingKey) =>
        !string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) >= MinimumKeyBytes;

    private static byte[] Key(string signingKey) => IsUsableKey(signingKey)
        ? Encoding.UTF8.GetBytes(signingKey)
        : throw new InvalidOperationException($"The coverage intake signing key must be at least {MinimumKeyBytes} bytes.");

    private static byte[] Sign(string encodedPayload, byte[] key) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Purpose + encodedPayload));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed record Payload(int V, string T, Guid R, string? P, long E);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

public interface IPublicWebsiteSchedulingService
{
    Task<IReadOnlyList<PublicSchedulingAvailabilitySlot>> GetAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Projects canonical availability into the versioned public envelope: the
    /// practice time zone plus slots whose timestamps are offset to that zone.
    /// </summary>
    Task<PublicAvailabilityView> GetPublishedAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default);
    Task<ValidatedPublicSchedulingSelection?> ValidateAsync(string tenantId, string token,
        PatientRelationship relationship, CancellationToken cancellationToken = default);
}

/// <summary>Projects canonical availability into a data-minimized website model.</summary>
public sealed class PublicWebsiteSchedulingService(
    SchedulingDbContext db, ISchedulingAvailabilityService availability, IConfiguration configuration)
    : IPublicWebsiteSchedulingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PublicSchedulingAvailabilitySlot>> GetAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var mappings = await Mappings(tenantId, cancellationToken);
        var providerId = InternalInt(mappings, SchedulingResourceType.Provider, request.ProviderCode);
        var locationId = InternalGuid(mappings, SchedulingResourceType.Location, request.LocationCode);
        var typeId = InternalString(mappings, SchedulingResourceType.VisitReason, request.AppointmentTypeCode);
        if ((request.ProviderCode is not null && providerId is null) ||
            (request.LocationCode is not null && locationId is null) ||
            (request.AppointmentTypeCode is not null && typeId is null)) return [];

        var slots = await availability.GetAvailabilityAsync(new SchedulingAvailabilityQuery
        {
            TenantId = tenantId, Channel = SchedulingChannel.PublicWebsite,
            PatientRelationship = request.PatientRelationship, FromUtc = request.From, ToUtc = request.To,
            ProviderId = providerId, LocationId = locationId, AppointmentTypeId = typeId
        }, cancellationToken);
        var types = await db.SchedulingAppointmentTypes.AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.AppointmentTypeId, cancellationToken);
        var mappingsByInternalId = mappings.ToDictionary(x => (x.ResourceType, x.InternalId));

        return slots.Select(slot =>
        {
            if (!mappingsByInternalId.TryGetValue((SchedulingResourceType.Provider, slot.ProviderId.ToString()), out var provider) ||
                !mappingsByInternalId.TryGetValue((SchedulingResourceType.Location, slot.LocationId.ToString()), out var location) ||
                !mappingsByInternalId.TryGetValue((SchedulingResourceType.VisitReason, slot.AppointmentTypeId), out var visit) ||
                !types.TryGetValue(slot.AppointmentTypeId, out var type)) return null;
            var payload = new SlotPayload(tenantId, slot.ProviderId, slot.LocationId, slot.AppointmentTypeId,
                slot.StartUtc, slot.EndUtc, request.PatientRelationship, DateTimeOffset.UtcNow.AddHours(24));
            return new PublicSchedulingAvailabilitySlot
            {
                AvailabilityToken = Protect(payload), AppointmentTypeCode = visit.ExternalId,
                AppointmentTypeName = type.DisplayName, ProviderCode = provider.ExternalId,
                ProviderName = provider.ExternalDisplayName, LocationCode = location.ExternalId,
                LocationName = location.ExternalDisplayName ?? "Practice location",
                Start = slot.StartUtc, End = slot.EndUtc
            };
        }).Where(x => x is not null).Cast<PublicSchedulingAvailabilitySlot>().ToList();
    }

    public async Task<PublicAvailabilityView> GetPublishedAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default)
    {
        var slots = await GetAsync(tenantId, request, cancellationToken);
        var timeZoneId = await db.SchedulingIntegrationConfigurations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Channel == SchedulingChannel.PublicWebsite)
            .Select(x => x.TimeZoneId).SingleOrDefaultAsync(cancellationToken);
        timeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId;
        var timeZone = ResolveTimeZone(timeZoneId);
        // When the time zone can't be resolved, fall back to UTC so the
        // returned TimeZone field is consistent with the (unconverted) timestamps.
        if (timeZone is null) timeZoneId = "UTC";
        // Present each bookable slot in the practice's local offset (the token
        // still encodes the canonical UTC instant, so booking revalidation is
        // unaffected by the display offset).
        var localized = timeZone is null ? slots : slots.Select(slot => slot with
        {
            Start = TimeZoneInfo.ConvertTime(slot.Start, timeZone),
            End = TimeZoneInfo.ConvertTime(slot.End, timeZone)
        }).ToList();
        return new PublicAvailabilityView
        {
            ProviderCode = request.ProviderCode,
            LocationCode = request.LocationCode,
            AppointmentTypeCode = request.AppointmentTypeCode,
            TimeZone = timeZoneId,
            From = request.From,
            To = request.To,
            Slots = localized
        };
    }

    private static TimeZoneInfo? ResolveTimeZone(string timeZoneId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    public async Task<ValidatedPublicSchedulingSelection?> ValidateAsync(string tenantId, string token,
        PatientRelationship relationship, CancellationToken cancellationToken = default)
    {
        var payload = Unprotect(token);
        if (payload is null || payload.TenantId != tenantId || payload.Relationship != relationship ||
            payload.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var slots = await availability.GetAvailabilityAsync(new SchedulingAvailabilityQuery
        {
            TenantId = tenantId, Channel = SchedulingChannel.PublicWebsite,
            ProviderId = payload.ProviderId, LocationId = payload.LocationId,
            AppointmentTypeId = payload.AppointmentTypeId, PatientRelationship = relationship,
            FromUtc = payload.Start, ToUtc = payload.End
        }, cancellationToken);
        var match = slots.SingleOrDefault(x => x.ProviderId == payload.ProviderId && x.LocationId == payload.LocationId &&
            x.AppointmentTypeId == payload.AppointmentTypeId && x.StartUtc == payload.Start && x.EndUtc == payload.End);
        return match is null ? null : new(match.ProviderId, match.LocationId, match.AppointmentTypeId, match.StartUtc, match.EndUtc);
    }

    private Task<List<ExternalSchedulingResourceMapping>> Mappings(string tenantId, CancellationToken cancellationToken) =>
        db.ExternalSchedulingResourceMappings.AsNoTracking().Where(x => x.TenantId == tenantId &&
            x.Channel == SchedulingChannel.PublicWebsite && x.IsActive).ToListAsync(cancellationToken);
    private static string? InternalString(IEnumerable<ExternalSchedulingResourceMapping> mappings,
        SchedulingResourceType type, string? code) => code is null ? null : mappings.SingleOrDefault(x => x.ResourceType == type && x.ExternalId == code)?.InternalId;
    private static int? InternalInt(IEnumerable<ExternalSchedulingResourceMapping> mappings, SchedulingResourceType type, string? code) =>
        int.TryParse(InternalString(mappings, type, code), out var id) ? id : null;
    private static Guid? InternalGuid(IEnumerable<ExternalSchedulingResourceMapping> mappings, SchedulingResourceType type, string? code) =>
        Guid.TryParse(InternalString(mappings, type, code), out var id) ? id : null;

    private string Protect(SlotPayload payload)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var protectedValue = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        protectedValue[0] = 1;
        nonce.CopyTo(protectedValue, 1);
        tag.CopyTo(protectedValue, 1 + nonce.Length);
        ciphertext.CopyTo(protectedValue, 1 + nonce.Length + tag.Length);
        return Base64Url(protectedValue);
    }
    private SlotPayload? Unprotect(string token)
    {
        try
        {
            var protectedValue = FromBase64Url(token);
            if (protectedValue.Length < 30 || protectedValue[0] != 1) return null;
            var nonce = protectedValue.AsSpan(1, 12);
            var tag = protectedValue.AsSpan(13, 16);
            var ciphertext = protectedValue.AsSpan(29);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(Key(), tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return JsonSerializer.Deserialize<SlotPayload>(plaintext, Json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException) { return null; }
    }
    private byte[] Key()
    {
        var value = configuration["PublicAvailability:SlotTokenKey"];
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
            throw new InvalidOperationException("Public availability slot encryption is not configured.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4); return Convert.FromBase64String(padded);
    }
    private static void ValidateRequest(PublicSchedulingAvailabilityRequest request)
    {
        if (request.PatientRelationship == PatientRelationship.Unknown) throw new ArgumentException("Patient relationship is required.");
        if (request.To <= request.From || request.To - request.From > TimeSpan.FromDays(31)) throw new ArgumentException("Availability range must be 31 days or less.");
    }
    private sealed record SlotPayload(string TenantId, int ProviderId, Guid LocationId, string AppointmentTypeId,
        DateTimeOffset Start, DateTimeOffset End, PatientRelationship Relationship, DateTimeOffset ExpiresAt);
}

public static class SchedulingInternalAuth
{
    public static string? ResolveTenant(HttpContext http, IConfiguration configuration)
    {
        var provided = http.Request.Headers["X-CDO-Service-Key"].ToString();
        if (string.IsNullOrWhiteSpace(provided)) return null;
        foreach (var client in configuration.GetSection("InternalApi:PublicIntakeClients").GetChildren())
        {
            var expected = client["ApiKey"]; var tenant = client["TenantId"];
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(tenant)) continue;
            var left = Encoding.UTF8.GetBytes(provided); var right = Encoding.UTF8.GetBytes(expected);
            if (left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right)) return tenant;
        }
        return null;
    }
}

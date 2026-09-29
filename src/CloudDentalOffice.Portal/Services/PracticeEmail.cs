using System.Net.Mail;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Per-practice addressing for patient email. Mail is sent from the platform's
/// address (<see cref="ReviewEmailOptions.FromAddress"/>, e.g. a clouddental.io
/// sending subdomain) under the practice's name, with replies going to the
/// practice's own mailbox.
/// </summary>
public sealed class PracticeEmailOptions
{
    public const string SectionName = "PracticeEmail";

    public List<PracticeEmailEntry> Practices { get; set; } = [];

    /// <summary>The practice's reply-to address, or null if none (or an invalid one) is configured.</summary>
    public string? ReplyToFor(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        var address = Practices.FirstOrDefault(p => string.Equals(p.TenantId, tenantId, StringComparison.Ordinal))?.ReplyTo?.Trim();
        // Parse the way the sender will, so a value MailAddress rejects is dropped here
        // instead of failing every message; a "Name <address>" form is not accepted.
        return !string.IsNullOrEmpty(address) && MailAddress.TryCreate(address, out var parsed) &&
               string.Equals(parsed.Address, address, StringComparison.Ordinal)
            ? address
            : null;
    }
}

public sealed class PracticeEmailEntry
{
    public string TenantId { get; set; } = string.Empty;
    public string? ReplyTo { get; set; }
}

public static class PracticeMail
{
    private const int MaxDisplayName = 70;

    /// <summary>
    /// A message from the platform address shown as the practice ("3rd Set Smiles
    /// &lt;no-reply@…&gt;"), replying to the practice when it has an address.
    /// </summary>
    public static MailMessage Create(string fromAddress, string? practiceName, string recipient, string? replyTo,
        string subject, string body, bool isHtml)
    {
        var message = new MailMessage(new MailAddress(fromAddress, DisplayName(practiceName)), new MailAddress(recipient))
        {
            Subject = subject, Body = body, IsBodyHtml = isHtml
        };
        if (!string.IsNullOrWhiteSpace(replyTo)) message.ReplyToList.Add(new MailAddress(replyTo));
        return message;
    }

    /// <summary>The practice name made safe for a From header: one line, no quotes or angle brackets.</summary>
    public static string? DisplayName(string? practiceName)
    {
        if (string.IsNullOrWhiteSpace(practiceName)) return null;
        var cleaned = new string(practiceName.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>')).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= MaxDisplayName ? cleaned : cleaned[..MaxDisplayName].TrimEnd();
    }
}

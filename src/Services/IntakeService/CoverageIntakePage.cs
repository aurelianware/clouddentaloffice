using System.Globalization;
using System.Net;
using System.Text;
using CloudDentalOffice.Contracts.Eligibility;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.AspNetCore.Http.Features;

/// <summary>What the patient typed. Every field is a raw form value until validated.</summary>
public sealed record CoverageIntakeForm(
    string? Answer, string? Carrier, string? MemberId, string? GroupNumber, string? Relationship,
    string? HolderFirstName, string? HolderLastName, string? HolderDateOfBirth)
{
    public static CoverageIntakeForm From(IFormCollection form) => new(
        form["answer"], form["carrier"], form["memberId"], form["groupNumber"], form["relationship"],
        form["holderFirstName"], form["holderLastName"], form["holderDob"]);
}

public static class CoverageIntakeValidator
{
    /// <summary>Field errors keyed by form field; empty when the answer can be sent.</summary>
    public static Dictionary<string, string> Validate(CoverageIntakeForm form, DateOnly today)
    {
        if (form.Answer is not ("plan" or "none"))
            return new() { ["answer"] = "Choose whether you have dental insurance." };
        if (form.Answer == "none") return [];

        DateOnly? dob = DateOnly.TryParseExact(form.HolderDateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;
        return CoverageIntakeAnswerRules.ValidatePlan(form.Carrier, form.MemberId, form.GroupNumber, form.Relationship,
            form.HolderFirstName, form.HolderLastName, dob, today);
    }

    public static CoverageIntakeSubmittedEvent ToEvent(CoverageIntakeForm form, CoverageIntakeTicket ticket, string token, DateTime now)
    {
        var plan = form.Answer == "plan";
        var self = form.Relationship == "Self";
        return new CoverageIntakeSubmittedEvent(ticket.TenantId, ticket.RequestId, token,
            plan ? CoverageIntakeSubmittedEvent.Plan : CoverageIntakeSubmittedEvent.NoInsurance)
        {
            // One answer per link: a resubmission is dropped by broker duplicate detection.
            EventId = Idempotency.CreateEventId(ticket.TenantId, ticket.RequestId.ToString("N")),
            OccurredAt = now,
            SubmittedAtUtc = now,
            CarrierName = plan ? form.Carrier!.Trim() : null,
            MemberId = plan ? form.MemberId!.Trim() : null,
            GroupNumber = plan && !string.IsNullOrWhiteSpace(form.GroupNumber) ? form.GroupNumber.Trim() : null,
            RelationshipToSubscriber = plan ? form.Relationship : null,
            SubscriberFirstName = plan && !self ? form.HolderFirstName!.Trim() : null,
            SubscriberLastName = plan && !self ? form.HolderLastName!.Trim() : null,
            SubscriberDateOfBirth = plan && !self
                ? DateOnly.ParseExact(form.HolderDateOfBirth!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null
        };
    }

}

/// <summary>
/// The page a patient reaches from a coverage intake email. The link's signed
/// token is the only credential; the answer is relayed to the private portal over
/// Service Bus and never stored here, keeping this service's database free of
/// patient data. Server-rendered HTML with no scripts, no referrer and no caching.
/// </summary>
public static class CoverageIntakePage
{
    /// <summary>The whole form is a few short fields; anything bigger is refused before it's buffered.</summary>
    public const int MaxFormBytes = 16 * 1024;

    private static readonly FormOptions FormLimits = new()
    {
        BufferBody = false, MultipartBodyLengthLimit = MaxFormBytes, ValueCountLimit = 20,
        ValueLengthLimit = 1024, KeyLengthLimit = 64
    };

    private const string UnavailableMessage =
        "This link has expired or isn't valid. Please call the office and they'll help you over the phone.";

    public static void MapCoverageIntake(this WebApplication app)
    {
        app.MapGet("/coverage/{token}", Show).RequireRateLimiting("coverage-intake").WithTags("CoverageIntake");
        // The token in the URL is the credential; there is no cookie to forge a request with.
        app.MapPost("/coverage/{token}", Submit).RequireRateLimiting("coverage-intake").WithTags("CoverageIntake")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxFormBytes))
            .DisableAntiforgery();
    }

    public static IResult Show(string token, IConfiguration configuration, TimeProvider time, HttpContext http)
    {
        if (!TryTicket(token, configuration, time, out var ticket)) return Page(http, Unavailable(), StatusCodes.Status404NotFound);
        return Page(http, Form(ticket, new CoverageIntakeForm(null, null, null, null, "Self", null, null, null), []));
    }

    public static async Task<IResult> Submit(string token, IConfiguration configuration, TimeProvider time,
        IEventPublisher publisher, ServiceBusOptions serviceBus, ILoggerFactory loggers, HttpContext http)
    {
        if (!TryTicket(token, configuration, time, out var ticket)) return Page(http, Unavailable(), StatusCodes.Status404NotFound);
        if (!http.Request.HasFormContentType) return Page(http, Unavailable(), StatusCodes.Status400BadRequest);
        if (http.Request.ContentLength > MaxFormBytes) return Page(http, Unavailable(), StatusCodes.Status413PayloadTooLarge);

        IFormCollection fields;
        try { fields = await http.Request.ReadFormAsync(FormLimits, http.RequestAborted); }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Page(http, Unavailable(), StatusCodes.Status400BadRequest);
        }
        var form = CoverageIntakeForm.From(fields);
        var now = time.GetUtcNow();
        var errors = CoverageIntakeValidator.Validate(form, DateOnly.FromDateTime(now.UtcDateTime));
        if (errors.Count > 0) return Page(http, Form(ticket, form, errors), StatusCodes.Status400BadRequest);

        if (!serviceBus.IsConfigured) return Page(http, TryLater(ticket), StatusCodes.Status503ServiceUnavailable);
        var evt = CoverageIntakeValidator.ToEvent(form, ticket, token, now.UtcDateTime);
        try
        {
            await publisher.PublishAsync(evt, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("CoverageIntake").LogError("Could not relay coverage intake {RequestId} ({FailureKind}).",
                ticket.RequestId, ex.GetType().Name);
            return Page(http, TryLater(ticket), StatusCodes.Status503ServiceUnavailable);
        }
        return Page(http, Thanks(ticket, form.Answer == "none"));
    }

    private static bool TryTicket(string token, IConfiguration configuration, TimeProvider time, out CoverageIntakeTicket ticket)
    {
        ticket = null!;
        var section = configuration.GetSection("CoverageIntake");
        var key = section["SigningKey"];
        return section.GetValue("Enabled", false) && CoverageIntakeToken.IsUsableKey(key) &&
               CoverageIntakeToken.TryRead(token, key!, time.GetUtcNow(), out ticket);
    }

    private static IResult Page(HttpContext http, string body, int status = StatusCodes.Status200OK)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        headers["X-Robots-Tag"] = "noindex";
        return Results.Content(Layout(body), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static string Form(CoverageIntakeTicket ticket, CoverageIntakeForm form, Dictionary<string, string> errors)
    {
        var practice = E(Practice(ticket));
        var html = new StringBuilder();
        html.Append($"<h1>Your dental insurance</h1><p>{practice} would like to confirm your dental coverage before your visit. It takes about a minute.</p>");
        if (errors.Count > 0)
            html.Append("<p class=\"alert\" role=\"alert\">Please check the highlighted answers below.</p>");
        html.Append("<form method=\"post\" novalidate>");

        html.Append("<fieldset><legend>Do you have dental insurance?</legend>");
        html.Append(Radio("answer", "plan", "Yes, I have dental insurance", form.Answer == "plan"));
        html.Append(Radio("answer", "none", "No, I don't have dental insurance", form.Answer == "none"));
        html.Append(Error(errors, "answer")).Append("</fieldset>");

        html.Append("<fieldset><legend>If you have dental insurance</legend>");
        html.Append("<p class=\"hint\">Use your <strong>dental</strong> card. A medical card usually won't cover dental visits.</p>");
        html.Append(Text("carrier", "Insurance company", form.Carrier, errors, "organization"));
        html.Append(Text("memberId", "Member ID", form.MemberId, errors, "off"));
        html.Append(Text("groupNumber", "Group number (if shown)", form.GroupNumber, errors, "off"));
        html.Append("<p class=\"label\">Whose name is the plan in?</p>");
        foreach (var (value, label) in new[] { ("Self", "Mine"), ("Spouse", "My spouse's"), ("Child", "My parent's"), ("Other", "Someone else's") })
            html.Append(Radio("relationship", value, label, form.Relationship == value));
        html.Append(Error(errors, "relationship"));
        html.Append("<p class=\"hint\">If the plan is in someone else's name, tell us about them:</p>");
        html.Append(Text("holderFirstName", "Plan holder's first name", form.HolderFirstName, errors, "off"));
        html.Append(Text("holderLastName", "Plan holder's last name", form.HolderLastName, errors, "off"));
        html.Append(Text("holderDob", "Plan holder's date of birth", form.HolderDateOfBirth, errors, "off", "date"));
        html.Append("</fieldset>");

        html.Append("<button type=\"submit\">Send to the office</button></form>");
        html.Append($"<p class=\"hint\">This secure link works until {ticket.ExpiresAt.UtcDateTime:MMMM d}. Questions? Call {practice}.</p>");
        return html.ToString();
    }

    private static string Thanks(CoverageIntakeTicket ticket, bool noInsurance) =>
        $"<h1>Thank you</h1><p>{E(Practice(ticket))} has your answer." +
        (noInsurance ? " They'll go over payment options with you at your visit." : " They'll confirm your coverage before your visit.") +
        "</p><p class=\"hint\">You can close this page.</p>";

    private static string TryLater(CoverageIntakeTicket ticket) =>
        $"<h1>Please try again shortly</h1><p>We couldn't send your answer to {E(Practice(ticket))} just now. " +
        "Go back and send it again in a few minutes.</p>";

    private static string Unavailable() => $"<h1>Link unavailable</h1><p>{E(UnavailableMessage)}</p>";

    private static string Practice(CoverageIntakeTicket ticket) =>
        string.IsNullOrWhiteSpace(ticket.PracticeName) ? "Your dental office" : ticket.PracticeName;

    private static string Radio(string name, string value, string label, bool isChecked) =>
        $"<label class=\"choice\"><input type=\"radio\" name=\"{name}\" value=\"{E(value)}\"{(isChecked ? " checked" : "")}> {E(label)}</label>";

    private static string Text(string name, string label, string? value, Dictionary<string, string> errors,
        string autocomplete, string type = "text") =>
        $"<label for=\"{name}\">{E(label)}</label>" +
        $"<input id=\"{name}\" name=\"{name}\" type=\"{type}\" value=\"{E(value)}\" autocomplete=\"{autocomplete}\"" +
        (errors.ContainsKey(name) ? $" aria-invalid=\"true\" aria-describedby=\"{name}-error\"" : "") + ">" + Error(errors, name);

    private static string Error(Dictionary<string, string> errors, string name) =>
        errors.TryGetValue(name, out var message) ? $"<p class=\"error\" id=\"{name}-error\">{E(message)}</p>" : "";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Layout(string body) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Your dental insurance</title>
        <style>
        :root { --bg:#f7f7f5; --card:#fff; --text:#1d1d1b; --muted:#5c5c58; --line:#d7d7d2; --accent:#1f5f8b; --error:#a3261c; color-scheme: light dark; }
        @media (prefers-color-scheme: dark) { :root { --bg:#141413; --card:#1e1e1c; --text:#ecece8; --muted:#a9a9a3; --line:#3a3a36; --accent:#79b4dd; --error:#f08b80; } }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width:34rem; margin:0 auto; padding:24px 16px 48px; }
        .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:20px; }
        h1 { font-size:1.5rem; margin:0 0 8px; }
        fieldset { border:1px solid var(--line); border-radius:8px; margin:16px 0; padding:12px 14px; }
        legend { font-weight:600; padding:0 4px; }
        label { display:block; margin-top:12px; font-weight:500; }
        label.choice { font-weight:400; margin-top:8px; }
        .label { margin:14px 0 0; font-weight:500; }
        input[type=text], input[type=date] { width:100%; margin-top:4px; padding:10px; font:inherit; color:inherit; background:transparent; border:1px solid var(--line); border-radius:6px; }
        input[aria-invalid=true] { border-color:var(--error); }
        .hint { color:var(--muted); font-size:.9rem; }
        .error, .alert { color:var(--error); font-size:.9rem; margin:4px 0 0; }
        button { margin-top:8px; width:100%; padding:12px; font:inherit; font-weight:600; color:#fff; background:var(--accent); border:0; border-radius:8px; cursor:pointer; }
        @media (prefers-color-scheme: dark) { button { color:#111; } }
        </style>
        </head>
        <body><main><div class="card">{{body}}</div></main></body>
        </html>
        """;
}

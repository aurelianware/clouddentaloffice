using CloudDentalOffice.Contracts.Eligibility;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

/// <summary>
/// The public coverage intake page: the signed link is the only credential, the
/// answer is relayed and never stored here. Synthetic data only.
/// </summary>
public sealed class CoverageIntakePageTests
{
    private const string Key = "test-only-coverage-intake-signing-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RequestId = Guid.Parse("7f1c2b0e-5a4d-4c1e-9f3a-2b6d8e0c1a11");

    // ── Token ──────────────────────────────────────────────────────────────

    [Fact]
    public void Token_round_trips_its_ticket()
    {
        var token = Token();

        Assert.True(CoverageIntakeToken.TryRead(token, Key, Now, out var ticket));
        Assert.Equal("practice-a", ticket.TenantId);
        Assert.Equal(RequestId, ticket.RequestId);
        Assert.Equal("Sunrise Dental", ticket.PracticeName);
    }

    [Fact]
    public void Tampered_expired_or_foreign_tokens_are_rejected()
    {
        var token = Token();
        var parts = token.Split('.');
        var forged = CoverageIntakeToken.Create(new("practice-b", RequestId, "Sunrise Dental", Now.AddDays(7)), Key).Split('.')[0];

        Assert.False(CoverageIntakeToken.TryRead($"{forged}.{parts[1]}", Key, Now, out _));
        Assert.False(CoverageIntakeToken.TryRead(token, Key.Replace('0', '1'), Now, out _));
        Assert.False(CoverageIntakeToken.TryRead(token, Key, Now.AddDays(8), out _));
        Assert.False(CoverageIntakeToken.TryRead("not-a-token", Key, Now, out _));
        Assert.False(CoverageIntakeToken.TryRead(token + ".x", Key, Now, out _));
    }

    [Fact]
    public void Short_signing_keys_are_refused()
    {
        Assert.False(CoverageIntakeToken.IsUsableKey("too-short"));
        Assert.Throws<InvalidOperationException>(() =>
            CoverageIntakeToken.Create(new("practice-a", RequestId, "x", Now.AddDays(1)), "too-short"));
    }

    // ── Validation ─────────────────────────────────────────────────────────

    [Fact]
    public void No_insurance_needs_nothing_else()
    {
        Assert.Empty(CoverageIntakeValidator.Validate(Form(answer: "none", carrier: null, memberId: null), Today));
    }

    [Fact]
    public void A_plan_needs_carrier_member_id_and_holder_details_when_not_self()
    {
        var errors = CoverageIntakeValidator.Validate(Form(carrier: " ", memberId: "AB#12", relationship: "Child"), Today);

        Assert.Contains("carrier", errors.Keys);
        Assert.Contains("memberId", errors.Keys);
        Assert.Contains("holderFirstName", errors.Keys);
        Assert.Contains("holderDob", errors.Keys);
        Assert.Contains("answer", CoverageIntakeValidator.Validate(Form(answer: null), Today).Keys);
        Assert.Contains("holderDob", CoverageIntakeValidator.Validate(
            Form(relationship: "Spouse", holderFirst: "Ana", holderLast: "Lee", holderDob: "2030-01-01"), Today).Keys);
    }

    [Fact]
    public void A_valid_plan_becomes_one_event_per_link()
    {
        var form = Form(relationship: "Child", holderFirst: " Ana ", holderLast: "Lee", holderDob: "1984-02-29");
        Assert.Empty(CoverageIntakeValidator.Validate(form, Today));
        var ticket = new CoverageIntakeTicket("practice-a", RequestId, "Sunrise Dental", Now.AddDays(7));

        var evt = CoverageIntakeValidator.ToEvent(form, ticket, "tok", Now.UtcDateTime);
        var again = CoverageIntakeValidator.ToEvent(form, ticket, "tok", Now.UtcDateTime);

        Assert.Equal(CoverageIntakeSubmittedEvent.Plan, evt.Answer);
        Assert.Equal("Delta Dental", evt.CarrierName);
        Assert.Equal("Ana", evt.SubscriberFirstName);
        Assert.Equal(new DateOnly(1984, 2, 29), evt.SubscriberDateOfBirth);
        Assert.Equal(evt.EventId, again.EventId);
    }

    // ── Page ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_link_shows_the_form_without_caching_or_referrer()
    {
        var http = Http();
        var result = CoverageIntakePage.Show(Token(), Config(), new Clock(Now), http);
        var body = await Run(result, http);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Contains("Sunrise Dental", body);
        Assert.Contains("<form method=\"post\"", body);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", http.Response.Headers["Referrer-Policy"].ToString());
        Assert.Contains("default-src 'none'", http.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task Invalid_expired_or_disabled_links_show_the_same_page()
    {
        foreach (var (token, config, now) in new[]
                 {
                     ("bogus", Config(), Now),
                     (Token(), Config(), Now.AddDays(8)),
                     (Token(), Config(enabled: false), Now)
                 })
        {
            var http = Http();
            var body = await Run(CoverageIntakePage.Show(token, config, new Clock(now), http), http);
            Assert.Equal(404, http.Response.StatusCode);
            Assert.Contains("This link has expired", body);
        }
    }

    [Fact]
    public async Task A_valid_answer_is_relayed_and_thanked()
    {
        var publisher = new RecordingPublisher();
        var http = Http(Fields(answer: "plan"));

        var body = await Run(await CoverageIntakePage.Submit(Token(), Config(), new Clock(Now), publisher,
            Bus(), NullLoggerFactory.Instance, http), http);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Contains("Thank you", body);
        var evt = Assert.IsType<CoverageIntakeSubmittedEvent>(Assert.Single(publisher.Published));
        Assert.Equal(RequestId, evt.RequestId);
        Assert.Equal("MBR-123", evt.MemberId);
        Assert.Equal(Token(), evt.Token);
    }

    [Fact]
    public async Task An_invalid_answer_is_shown_again_with_errors_and_not_relayed()
    {
        var publisher = new RecordingPublisher();
        var http = Http(new() { ["answer"] = "plan", ["carrier"] = "<script>x</script>", ["relationship"] = "Self" });

        var body = await Run(await CoverageIntakePage.Submit(Token(), Config(), new Clock(Now), publisher,
            Bus(), NullLoggerFactory.Instance, http), http);

        Assert.Equal(400, http.Response.StatusCode);
        Assert.Contains("Enter the member ID", body);
        Assert.Contains("&lt;script&gt;", body);
        Assert.DoesNotContain("<script>", body);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task No_broker_or_a_failed_publish_asks_the_patient_to_try_again()
    {
        var http = Http(Fields(answer: "none"));
        await Run(await CoverageIntakePage.Submit(Token(), Config(), new Clock(Now), new RecordingPublisher(),
            new ServiceBusOptions(), NullLoggerFactory.Instance, http), http);
        Assert.Equal(503, http.Response.StatusCode);

        http = Http(Fields(answer: "none"));
        var body = await Run(await CoverageIntakePage.Submit(Token(), Config(), new Clock(Now),
            new RecordingPublisher { Fail = true }, Bus(), NullLoggerFactory.Instance, http), http);
        Assert.Equal(503, http.Response.StatusCode);
        Assert.Contains("try again", body);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    private static string Token() =>
        CoverageIntakeToken.Create(new("practice-a", RequestId, "Sunrise Dental", Now.AddDays(7)), Key);

    private static CoverageIntakeForm Form(string? answer = "plan", string? carrier = "Delta Dental", string? memberId = "MBR-123",
        string? relationship = "Self", string? holderFirst = null, string? holderLast = null, string? holderDob = null) =>
        new(answer, carrier, memberId, null, relationship, holderFirst, holderLast, holderDob);

    private static Dictionary<string, StringValues> Fields(string answer) => new()
    {
        ["answer"] = answer, ["carrier"] = "Delta Dental", ["memberId"] = "MBR-123", ["relationship"] = "Self"
    };

    private static IConfiguration Config(bool enabled = true) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["CoverageIntake:Enabled"] = enabled.ToString(), ["CoverageIntake:SigningKey"] = Key }).Build();

    private static ServiceBusOptions Bus() => new() { ConnectionString = "Endpoint=sb://test/;SharedAccessKeyName=k;SharedAccessKey=v" };

    private static DefaultHttpContext Http(Dictionary<string, StringValues>? form = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        http.Response.Body = new MemoryStream();
        if (form is not null)
        {
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = new FormCollection(form);
        }
        return http;
    }

    private static async Task<string> Run(IResult result, HttpContext http)
    {
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool Fail { get; init; }
        public List<IntegrationEvent> Published { get; } = [];

        public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Broker down.");
            Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

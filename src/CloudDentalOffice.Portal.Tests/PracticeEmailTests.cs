using CloudDentalOffice.Portal.Services;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>Patient email goes out from the platform address under the practice's name, replying to the practice.</summary>
public sealed class PracticeEmailTests
{
    [Fact]
    public void Mail_is_from_the_platform_address_as_the_practice_and_replies_to_the_practice()
    {
        using var mail = PracticeMail.Create("no-reply@mail.clouddental.test", "3rd Set Smiles", "patient@example.test",
            "info@practice.test", "Subject", "Body", isHtml: false);

        Assert.Equal("no-reply@mail.clouddental.test", mail.From!.Address);
        Assert.Equal("3rd Set Smiles", mail.From.DisplayName);
        Assert.Equal("info@practice.test", Assert.Single(mail.ReplyToList).Address);
        Assert.Equal("patient@example.test", Assert.Single(mail.To).Address);
    }

    [Fact]
    public void Without_a_practice_address_there_is_no_reply_to()
    {
        using var mail = PracticeMail.Create("no-reply@mail.clouddental.test", null, "patient@example.test",
            null, "Subject", "Body", isHtml: false);

        Assert.Empty(mail.ReplyToList);
        Assert.Equal("", mail.From!.DisplayName);
    }

    [Theory]
    [InlineData("Sunrise \"Dental\" <Group>", "Sunrise Dental Group")]
    [InlineData("Sunrise\r\nBcc: someone@example.test", "SunriseBcc: someone@example.test")]
    [InlineData("  ", null)]
    public void Practice_names_are_made_safe_for_the_from_header(string name, string? expected)
    {
        Assert.Equal(expected, PracticeMail.DisplayName(name));
        Assert.Equal(70, PracticeMail.DisplayName(new string('x', 200))!.Length);
    }

    [Fact]
    public void Reply_to_is_per_practice_and_must_be_an_address()
    {
        var options = new PracticeEmailOptions
        {
            Practices =
            [
                new() { TenantId = "practice-a", ReplyTo = " info@practice-a.test " },
                new() { TenantId = "practice-b", ReplyTo = "not an address" }
            ]
        };

        Assert.Equal("info@practice-a.test", options.ReplyToFor("practice-a"));
        Assert.Null(options.ReplyToFor("practice-b"));
        Assert.Null(options.ReplyToFor("practice-c"));
        Assert.Null(options.ReplyToFor(null));
    }
}

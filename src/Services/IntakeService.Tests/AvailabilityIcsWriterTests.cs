using CloudDentalOffice.Contracts.Scheduling;

public sealed class AvailabilityIcsWriterTests
{
    private static PublicSchedulingAvailabilitySlot Slot(string token, int startHour, string typeName, string locationName) => new()
    {
        AvailabilityToken = token,
        AppointmentTypeCode = "new-exam",
        AppointmentTypeName = typeName,
        ProviderCode = "dr-phillips",
        ProviderName = "Dr. Phillips",
        LocationCode = "tempe",
        LocationName = locationName,
        Start = new DateTimeOffset(2030, 8, 20, startHour, 0, 0, TimeSpan.FromHours(-7)),
        End = new DateTimeOffset(2030, 8, 20, startHour + 1, 0, 0, TimeSpan.FromHours(-7))
    };

    private static PublicAvailabilityView View(params PublicSchedulingAvailabilitySlot[] slots) => new()
    {
        TimeZone = "America/Phoenix",
        From = new DateTimeOffset(2030, 8, 20, 0, 0, 0, TimeSpan.Zero),
        To = new DateTimeOffset(2030, 8, 27, 0, 0, 0, TimeSpan.Zero),
        Slots = slots
    };

    [Fact]
    public void EmitsOneBookableEventPerSlotInUtc()
    {
        var ics = AvailabilityIcsWriter.Write(
            View(Slot("token-a", 9, "New patient exam", "Tempe office"),
                 Slot("token-b", 14, "New patient exam", "Tempe office")),
            DateTime.UtcNow);

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Equal(2, Occurrences(ics, "BEGIN:VEVENT"));
        // 09:00 -07:00 == 16:00Z; slots are published as free/transparent time.
        Assert.Contains("DTSTART:20300820T160000Z\r\n", ics);
        Assert.Contains("DTEND:20300820T170000Z\r\n", ics);
        Assert.Contains("TRANSP:TRANSPARENT\r\n", ics);
        Assert.Contains("SUMMARY:Available", ics);
    }

    [Fact]
    public void ContainsNoPhiOrRawTokens()
    {
        var ics = AvailabilityIcsWriter.Write(View(Slot("secret-token", 9, "New Exam", "Tempe office")), DateTime.UtcNow);

        // The opaque booking token must never appear verbatim; only its hash is used as a UID.
        Assert.DoesNotContain("secret-token", ics);
        // No attendee/organizer or contact channels through which PHI could travel.
        foreach (var forbidden in new[] { "ATTENDEE", "ORGANIZER", "phone", "email", "@example" })
            Assert.DoesNotContain(forbidden, ics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EscapesTextSpecialCharacters()
    {
        var ics = AvailabilityIcsWriter.Write(View(Slot("token-a", 9, "Exam, deluxe; special", "Tempe, AZ office")), DateTime.UtcNow);

        Assert.Contains("SUMMARY:Available — Exam\\, deluxe\\; special\r\n", ics);
        Assert.Contains("LOCATION:Tempe\\, AZ office\r\n", ics);
    }

    [Fact]
    public void EmptyAvailabilityStillProducesAValidCalendar()
    {
        var ics = AvailabilityIcsWriter.Write(View(), DateTime.UtcNow);

        Assert.Contains("BEGIN:VCALENDAR\r\n", ics);
        Assert.Equal(0, Occurrences(ics, "BEGIN:VEVENT"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }
}

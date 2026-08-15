using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Portal.Services;

public sealed class BookingRequestDisplayTests
{
    [Fact]
    public void MissingOptionalFieldsRenderWithSafeFallbacks()
    {
        var request = new BookingRequestDto { Phone = "4805550100", Source = "PublicWebsite" };

        Assert.Equal("4805550100", BookingRequestDisplay.Contact(request));
        Assert.Equal("Not provided", BookingRequestDisplay.Insurance(request));
        Assert.Equal("PublicWebsite", BookingRequestDisplay.Acquisition(request));
    }
}

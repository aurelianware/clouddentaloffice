using CloudDentalOffice.Contracts.Scheduling;

namespace CloudDentalOffice.Portal.Services;

public static class BookingRequestDisplay
{
    public static string Contact(BookingRequestDto request) => request.PreferredContact switch
    {
        "Email" when !string.IsNullOrWhiteSpace(request.Email) => $"Email · {request.Email}",
        "Phone" or "Text" => $"{request.PreferredContact} · {request.Phone}",
        _ when !string.IsNullOrWhiteSpace(request.Phone) => request.Phone,
        _ when !string.IsNullOrWhiteSpace(request.Email) => request.Email,
        _ => "Not provided"
    };

    public static string Insurance(BookingRequestDto request) =>
        string.IsNullOrWhiteSpace(request.InsuranceIntent) ? "Not provided"
        : string.IsNullOrWhiteSpace(request.InsuranceCarrier) ? request.InsuranceIntent
        : $"{request.InsuranceIntent} · {request.InsuranceCarrier}";

    public static string Acquisition(BookingRequestDto request) =>
        string.IsNullOrWhiteSpace(request.Campaign) ? request.Source : $"{request.Source} · {request.Campaign}";
}

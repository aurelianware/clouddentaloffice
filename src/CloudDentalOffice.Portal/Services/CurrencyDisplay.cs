using System.Globalization;

namespace CloudDentalOffice.Portal.Services;

/// <summary>USD display that also works on the production Alpine runtime without ICU.</summary>
public static class CurrencyDisplay
{
    private static readonly NumberFormatInfo UsdFormat = CreateUsdFormat();

    private static NumberFormatInfo CreateUsdFormat()
    {
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.CurrencySymbol = "$";
        format.CurrencyPositivePattern = 0;
        format.CurrencyNegativePattern = 1;
        return NumberFormatInfo.ReadOnly(format);
    }

    public static string Usd(decimal amount) => amount.ToString("C2", UsdFormat);
}

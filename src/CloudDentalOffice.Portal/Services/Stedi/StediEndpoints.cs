namespace CloudDentalOffice.Portal.Services.Stedi;

/// <summary>
/// A practice's Stedi key rides on every request, so a request may only go to
/// the configured HTTPS origin. An absolute or protocol-relative path would
/// replace the base URL, and user info could redirect credentials.
/// </summary>
internal static class StediEndpoints
{
    public static Uri? Resolve(string? baseUrl, string? path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo))
            return null;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') || !Uri.TryCreate(path, UriKind.Relative, out _))
            return null;
        var target = new Uri(baseUri, path);
        return target.Scheme == Uri.UriSchemeHttps &&
               string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               target.Port == baseUri.Port
            ? target
            : null;
    }
}

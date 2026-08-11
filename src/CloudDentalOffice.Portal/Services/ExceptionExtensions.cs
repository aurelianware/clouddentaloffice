namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Helpers for turning exceptions into actionable messages.
/// EF Core wraps the real cause of a failed save in
/// <c>DbUpdateException.InnerException</c> and only reports
/// "An error occurred while saving the entity changes. See the inner exception
/// for details." at the top level. These helpers unwrap that chain so both logs
/// and user-facing messages describe what actually went wrong.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    /// Returns a message that includes the deepest inner exception, which is
    /// where the real database/validation error lives. The result is collapsed to
    /// a single line so it stays readable in snackbars and safe to reuse in logs
    /// (exception messages can contain newlines or other untrusted text).
    /// </summary>
    public static string GetDetailedMessage(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var outer = Flatten(exception.Message);
        var root = exception.GetBaseException();
        var inner = Flatten(root.Message);

        // Nothing extra to add when there's no distinct underlying cause.
        if (ReferenceEquals(root, exception)
            || string.IsNullOrWhiteSpace(inner)
            || string.Equals(outer, inner, StringComparison.Ordinal))
        {
            return outer;
        }

        // Surface the outer context plus the underlying cause.
        return $"{outer} ({inner})";
    }

    /// <summary>
    /// Collapses any run of whitespace (including newlines and tabs) into single
    /// spaces so a message renders on one line.
    /// </summary>
    private static string Flatten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

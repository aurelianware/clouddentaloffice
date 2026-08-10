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
    /// where the real database/validation error lives.
    /// </summary>
    public static string GetDetailedMessage(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (ReferenceEquals(root, exception) || string.IsNullOrWhiteSpace(root.Message))
        {
            return exception.Message;
        }

        // Surface the outer context plus the underlying cause.
        return $"{exception.Message} ({root.Message})";
    }
}

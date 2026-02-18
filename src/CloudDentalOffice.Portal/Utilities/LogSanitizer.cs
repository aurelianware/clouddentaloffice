namespace CloudDentalOffice.Portal.Utilities;

/// <summary>
/// Utility for sanitizing user-controlled values before logging to prevent log forging attacks.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes a string value for safe logging by replacing control characters with visible escape sequences.
    /// This prevents log forging attacks where an attacker could inject additional log lines via user-controlled input.
    /// </summary>
    /// <param name="value">The value to sanitize. Can be null or empty.</param>
    /// <returns>The sanitized value with control characters replaced by literal escape sequences, or the original value if null/empty.</returns>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Replace common control characters with visible escape sequences to prevent log forging
        // while preserving the original structure of the value.
        // We use sequential Replace() which is simple and efficient for the most common cases.
        return value
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}

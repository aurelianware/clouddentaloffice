namespace CloudDentalOffice.Portal.Utilities;

/// <summary>
/// Utility for sanitizing user-controlled values before logging to prevent log forging attacks.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes a string value for safe logging by replacing newline characters with visible escape sequences.
    /// This prevents log forging attacks where an attacker could inject additional log lines via user-controlled input.
    /// </summary>
    /// <param name="value">The value to sanitize. Can be null or empty.</param>
    /// <returns>The sanitized value with CR/LF replaced by literal "\r" and "\n" sequences, or the original value if null/empty.</returns>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Replace newline characters with visible escape sequences to prevent log forging
        // while preserving the original structure of the value.
        return value
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }
}

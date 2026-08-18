public static class SchedulingTime
{
    /// <summary>
    /// Scheduling persistence is UTC. Offset-less values are interpreted as
    /// already-UTC for compatibility with the existing booking event contract;
    /// local values are converted using their supplied local kind.
    /// </summary>
    public static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? NormalizeUtc(DateTime? value) =>
        value.HasValue ? NormalizeUtc(value.Value) : null;
}

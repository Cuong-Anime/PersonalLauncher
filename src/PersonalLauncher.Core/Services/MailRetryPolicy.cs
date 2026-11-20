namespace PersonalLauncher.Core.Services;

public static class MailRetryPolicy
{
    private static readonly TimeSpan[] BaseDelays =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1)
    };

    /// <summary>
    /// Computes next retry delay: 1m -> 5m -> 15m -> 1h (capped at 1h) with ±15% jitter.
    /// </summary>
    public static TimeSpan GetNextDelay(int retryCount, double jitterUnitZeroToOne)
    {
        int index = Math.Clamp(retryCount, 0, BaseDelays.Length - 1);
        double baseSeconds = BaseDelays[index].TotalSeconds;
        double clampedJitter = Math.Clamp(jitterUnitZeroToOne, 0.0, 1.0);
        double multiplier = 0.85 + (clampedJitter * 0.30); // [0.85, 1.15]
        return TimeSpan.FromSeconds(Math.Max(5, baseSeconds * multiplier));
    }
}

namespace Praxy.Webhooks;

/// <summary>
/// Full-jitter exponential backoff — <c>uniform(0, min(cap, base·2^(attempt-1)))</c>. The same
/// "full jitter, not lockstep" shape research/flutter-sdk.md documents for the realtime client's
/// reconnect, mirrored server-side per the Phase 6 prompt so a burst of deliveries failing at once
/// (an endpoint's brief outage) doesn't retry in lockstep and DDoS it the moment it recovers.
/// </summary>
public static class WebhookBackoff
{
    /// <param name="attempt">The 1-based attempt number that just failed.</param>
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan cap)
    {
        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(cap.TotalMilliseconds, exponential);
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * capped);
    }
}

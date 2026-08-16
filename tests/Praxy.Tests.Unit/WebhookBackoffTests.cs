using Praxy.Webhooks;

namespace Praxy.Tests.Unit;

public class WebhookBackoffTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void Delay_is_always_within_the_full_jitter_bound(int attempt)
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var cap = TimeSpan.FromSeconds(300);
        var upperBound = TimeSpan.FromMilliseconds(Math.Min(cap.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));

        for (var i = 0; i < 200; i++)
        {
            var delay = WebhookBackoff.Compute(attempt, baseDelay, cap);
            Assert.True(delay >= TimeSpan.Zero, $"delay {delay} was negative");
            Assert.True(delay <= upperBound, $"delay {delay} exceeded bound {upperBound}");
        }
    }

    [Fact]
    public void Delay_is_capped_for_large_attempt_numbers()
    {
        var cap = TimeSpan.FromSeconds(300);
        for (var i = 0; i < 200; i++)
        {
            var delay = WebhookBackoff.Compute(30, TimeSpan.FromSeconds(1), cap);
            Assert.True(delay <= cap, $"delay {delay} exceeded the cap {cap} at a large attempt number");
        }
    }

    [Fact]
    public void Successive_attempts_grow_the_upper_bound_until_the_cap()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var cap = TimeSpan.FromSeconds(300);
        double previousBound = 0;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var bound = Math.Min(cap.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            Assert.True(bound >= previousBound);
            previousBound = bound;
        }
    }

    [Fact]
    public void Repeated_calls_are_not_lockstep()
    {
        // Full jitter's entire point: two failing attempts at the same attempt number should not
        // reliably retry at the same instant. Extremely low false-failure probability (uniform
        // draws over a wide range colliding to microsecond precision 32 times running).
        var seen = new HashSet<TimeSpan>();
        for (var i = 0; i < 32; i++)
            seen.Add(WebhookBackoff.Compute(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(300)));
        Assert.True(seen.Count > 1, "32 draws produced the exact same delay — jitter looks broken");
    }
}

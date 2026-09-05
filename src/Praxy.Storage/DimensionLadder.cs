namespace Praxy.Storage;

/// <summary>
/// The fixed rungs a requested transform dimension snaps up to. This is the security control the
/// whole feature hangs off (docs/research/storage.md's "Bounding the URL space"): without it,
/// <c>?width=1..2000</c> against one public image creates two thousand cached derivatives from a
/// single source. Snapping up to one of six rungs makes the per-file key space small and fixed
/// instead of attacker-controlled.
///
/// <para>
/// Above the top rung is rejected rather than clamped — <see cref="SnapUp"/> returns <c>null</c>
/// rather than <see cref="Rungs"/>[^1], because silently returning a smaller image than asked for is
/// the kind of surprise that costs an afternoon to debug, not a courtesy.
/// </para>
/// </summary>
public static class DimensionLadder
{
    public static readonly IReadOnlyList<int> Rungs = [64, 128, 256, 512, 1024, 2048];

    public static int TopRung => Rungs[^1];

    /// <summary>The smallest rung at or above <paramref name="requested"/>, or <c>null</c> above <see cref="TopRung"/>.</summary>
    public static int? SnapUp(int requested)
    {
        foreach (var rung in Rungs)
        {
            if (requested <= rung) return rung;
        }
        return null;
    }
}

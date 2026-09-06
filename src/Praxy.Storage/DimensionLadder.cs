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

    /// <summary>
    /// The ceiling on a derivative's <em>total</em> pixels, which bounds the one thing a single-axis
    /// request can otherwise make unbounded: the allocation.
    ///
    /// <para>Only the caller-named axis snaps to a rung; the other is derived from the source's real
    /// aspect ratio, and an extreme ratio derives an enormous value from a small request (a real
    /// 4x100,000 screenshot at <c>?width=64</c> derives a height of 1,600,000 — a ~410 MB target
    /// bitmap, security-review-phase-2 finding A). That has to be rejected.</para>
    ///
    /// <para><b>Bounding the derived <em>axis</em> at <see cref="TopRung"/> is the wrong shape for
    /// it</b>, even though it looks symmetric with the requested axis: it rejects every ordinary
    /// photo at the top rung, because only a perfectly square source can have both axes land on or
    /// under 2048. A 3:4 phone photo at <c>?width=2048</c> derives 2731; 9:16 derives 3641; an A4
    /// scan derives 2897. All are entirely reasonable derivatives — 2048x2731 is ~22 MB, not a
    /// threat — and all would 400. The rung exists to bound the <em>key space</em> of the axis the
    /// caller can vary, which the derived axis by definition isn't; what the derived axis needs
    /// bounded is cost, and cost is area.</para>
    ///
    /// <para>Two top rungs' worth (2048x4096) admits every realistic photo and document ratio up to
    /// 1:2 at full size, while a 1:3-or-wider panorama has to ask for a smaller width — which still
    /// works at every lower rung, since the bound is on the product, not either axis.</para>
    /// </summary>
    public static int MaxOutputPixels => TopRung * TopRung * 2;

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

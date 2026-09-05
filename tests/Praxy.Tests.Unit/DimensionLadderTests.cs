using Praxy.Storage;

namespace Praxy.Tests.Unit;

public class DimensionLadderTests
{
    [Fact]
    public void Below_the_first_rung_snaps_up_to_it()
    {
        Assert.Equal(64, DimensionLadder.SnapUp(1));
        Assert.Equal(64, DimensionLadder.SnapUp(63));
    }

    [Fact]
    public void Exactly_on_a_rung_stays_there()
    {
        Assert.Equal(256, DimensionLadder.SnapUp(256));
        Assert.Equal(2048, DimensionLadder.SnapUp(2048));
    }

    [Fact]
    public void Between_two_rungs_snaps_up_to_the_higher_one()
    {
        Assert.Equal(256, DimensionLadder.SnapUp(200));
        Assert.Equal(512, DimensionLadder.SnapUp(257));
    }

    [Fact]
    public void Above_the_top_rung_is_rejected_not_clamped()
    {
        Assert.Null(DimensionLadder.SnapUp(2049));
        Assert.Null(DimensionLadder.SnapUp(int.MaxValue));
    }
}

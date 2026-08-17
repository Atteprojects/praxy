using Praxy.Tables.Quotas;

namespace Praxy.Tests.Unit;

public class OrganizationLimitsTests
{
    [Fact]
    public void Roundtrips_through_json()
    {
        var limits = new OrganizationLimits(MaxProjects: 5, MaxDatabasesPerProject: 3);
        var parsed = OrganizationLimits.Parse(limits.ToJson());
        Assert.Equal(limits, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Unset_or_unreadable_limits_mean_unlimited_not_zero(string? json)
    {
        var limits = OrganizationLimits.Parse(json);
        Assert.Null(limits.MaxProjects);
        Assert.Null(limits.MaxDatabasesPerProject);
        Assert.Null(limits.MaxTablesPerDatabase);
        Assert.Null(limits.MaxColumnsPerTable);
        Assert.Null(limits.MaxIndexesPerTable);
    }

    [Fact]
    public void A_partial_override_leaves_other_dimensions_unset()
    {
        var limits = OrganizationLimits.Parse("""{"maxDatabasesPerProject": 2}""");
        Assert.Equal(2, limits.MaxDatabasesPerProject);
        Assert.Null(limits.MaxProjects);
        Assert.Null(limits.MaxTablesPerDatabase);
    }
}

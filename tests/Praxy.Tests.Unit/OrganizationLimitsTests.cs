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

    /// <summary>
    /// Storage's three dimensions ride the existing jsonb override mechanism unchanged — the two
    /// byte-valued ones are <c>long</c>, since a project storage budget past 2GB is ordinary.
    /// </summary>
    [Fact]
    public void Storage_dimensions_override_through_the_same_jsonb()
    {
        var limits = OrganizationLimits.Parse(
            """{"maxBucketsPerProject": 3, "maxFileSizeBytes": 104857600, "maxStorageBytesPerProject": 10737418240}""");

        Assert.Equal(3, limits.MaxBucketsPerProject);
        Assert.Equal(104_857_600L, limits.MaxFileSizeBytes);
        Assert.Equal(10_737_418_240L, limits.MaxStorageBytesPerProject);
        Assert.Null(limits.MaxProjects);
    }

    [Fact]
    public void Unset_storage_dimensions_are_null_so_the_instance_default_applies()
    {
        var limits = OrganizationLimits.Parse("{}");
        Assert.Null(limits.MaxBucketsPerProject);
        Assert.Null(limits.MaxFileSizeBytes);
        Assert.Null(limits.MaxStorageBytesPerProject);
    }
}

public class StorageBudgetTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(40, 60)]
    [InlineData(100, 0)]
    // Already over budget (an org's limit lowered after the files were stored): remaining clamps to
    // zero rather than going negative, so the next upload is rejected instead of being handed a
    // nonsensical allowance.
    [InlineData(150, 0)]
    public void Remaining_is_the_unused_budget_and_never_negative(long used, long expected) =>
        Assert.Equal(expected, new StorageBudget(MaxFileSizeBytes: 10, MaxTotalBytes: 100, UsedBytes: used).Remaining);
}

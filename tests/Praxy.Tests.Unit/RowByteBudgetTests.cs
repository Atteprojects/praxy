using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class RowByteBudgetTests
{
    [Fact]
    public void Small_columns_fit_comfortably()
    {
        var columns = new[]
        {
            new RowByteBudget.ColumnEstimate("title", RowByteBudget.EstimateBytes(ColumnTypes.String, 128, false, null)),
            new RowByteBudget.ColumnEstimate("age", RowByteBudget.EstimateBytes(ColumnTypes.Integer, null, false, null)),
            new RowByteBudget.ColumnEstimate("active", RowByteBudget.EstimateBytes(ColumnTypes.Boolean, null, false, null)),
        };
        RowByteBudget.Assert(columns); // must not throw
    }

    [Fact]
    public void Oversized_string_column_trips_the_budget()
    {
        var columns = new[]
        {
            new RowByteBudget.ColumnEstimate("huge", RowByteBudget.EstimateBytes(ColumnTypes.String, 10_000, false, null)),
        };
        var ex = Assert.Throws<RowSizeExceededException>(() => RowByteBudget.Assert(columns));
        Assert.True(ex.TotalBytes > ex.BudgetBytes);
    }

    [Fact]
    public void Exception_names_the_largest_offenders_first()
    {
        var columns = new[]
        {
            new RowByteBudget.ColumnEstimate("small", 10),
            new RowByteBudget.ColumnEstimate("biggest", 9_000),
            new RowByteBudget.ColumnEstimate("medium", 500),
        };
        var ex = Assert.Throws<RowSizeExceededException>(() => RowByteBudget.Assert(columns));
        Assert.Equal("biggest", ex.LargestColumns[0].Key);
    }

    [Fact]
    public void Custom_budget_is_honored()
    {
        var columns = new[] { new RowByteBudget.ColumnEstimate("x", 100) };
        Assert.Throws<RowSizeExceededException>(() => RowByteBudget.Assert(columns, budgetBytes: 50));
        RowByteBudget.Assert(columns, budgetBytes: 1000); // must not throw
    }

    [Fact]
    public void Array_estimate_is_capped_not_unbounded()
    {
        var scalarEstimate = RowByteBudget.EstimateBytes(ColumnTypes.String, 1000, isArray: false, null);
        var arrayEstimate = RowByteBudget.EstimateBytes(ColumnTypes.String, 1000, isArray: true, null);
        Assert.True(arrayEstimate < scalarEstimate * 10);
    }

    [Fact]
    public void Enum_estimate_uses_the_longest_declared_element()
    {
        var bytes = RowByteBudget.EstimateBytes(ColumnTypes.Enum, null, false, ["a", "much-longer-element"]);
        Assert.Equal("much-longer-element".Length, bytes);
    }

    [Fact]
    public void Relationship_estimate_is_uuid_width_scalar_and_capped_when_array()
    {
        Assert.Equal(16, RowByteBudget.EstimateBytes(ColumnTypes.Relationship, null, false, null));
        Assert.Equal(160, RowByteBudget.EstimateBytes(ColumnTypes.Relationship, null, true, null));
    }
}

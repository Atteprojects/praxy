namespace Praxy.Tables;

/// <summary>
/// Estimates a table's worst-case row width and enforces a configurable budget, per
/// roadmap.md's "row byte budget computed and enforced at definition time with named-offender
/// errors." This is a declared-max-size sanity check, not a true TOAST-aware Postgres accounting
/// (Postgres moves large values out-of-line automatically) — it exists to catch schemas that would
/// need TOAST on every row before they're built, not to model Postgres storage exactly.
/// </summary>
public static class RowByteBudget
{
    /// <summary>Default budget: a Postgres heap page (8KB) minus page/tuple-header overhead.</summary>
    public const int DefaultBudgetBytes = 8_000;

    /// <summary>System columns: _id (uuid, 16) + _created_at/_updated_at (timestamptz, 8 each).</summary>
    public const int SystemColumnBytes = 16 + 8 + 8;

    public sealed record ColumnEstimate(string Key, int Bytes);

    /// <summary>Worst-case bytes for one column: strings count their declared UTF-8-worst-case size.</summary>
    public static int EstimateBytes(string type, int? size, bool isArray, string[]? enumElements)
    {
        var scalar = type switch
        {
            ColumnTypes.String => (size ?? 0) * 4, // UTF-8 worst case: 4 bytes/char
            ColumnTypes.Integer => 8,
            ColumnTypes.Float => 8,
            ColumnTypes.Boolean => 1,
            ColumnTypes.Datetime => 8,
            ColumnTypes.Email => 320,
            ColumnTypes.Url => 2048,
            ColumnTypes.Ip => 45, // longest IPv6 text form
            ColumnTypes.Enum => enumElements is { Length: > 0 } elements
                ? elements.Max(e => e.Length)
                : ColumnTypes.MaxEnumElementLength,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type."),
        };

        // Arrays have no declared length cap; assume a conservative 10-element worst case,
        // capped so one array column can't blow the whole budget on its own.
        return isArray ? Math.Min(scalar * 10, 4000) : scalar;
    }

    /// <summary>
    /// Throws <see cref="RowSizeExceededException"/> naming the largest offenders when the total
    /// (system columns + every column's estimate) exceeds <paramref name="budgetBytes"/>.
    /// </summary>
    public static void Assert(IEnumerable<ColumnEstimate> columns, int budgetBytes = DefaultBudgetBytes)
    {
        var list = columns.ToList();
        var total = SystemColumnBytes + list.Sum(c => c.Bytes);
        if (total <= budgetBytes)
            return;

        var offenders = list.OrderByDescending(c => c.Bytes).Take(5).ToArray();
        throw new RowSizeExceededException(total, budgetBytes, offenders);
    }
}

public sealed class RowSizeExceededException(
    int totalBytes, int budgetBytes, RowByteBudget.ColumnEstimate[] largestColumns)
    : Exception($"Row would need ~{totalBytes} bytes, over the {budgetBytes}-byte budget.")
{
    public int TotalBytes { get; } = totalBytes;
    public int BudgetBytes { get; } = budgetBytes;
    public RowByteBudget.ColumnEstimate[] LargestColumns { get; } = largestColumns;
}

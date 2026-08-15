using System.Text.RegularExpressions;
using Praxy.Core.Errors;

namespace Praxy.Tests.Unit;

public class ErrorTypesTests
{
    // Error `type` strings are public API. Appwrite shipped two accidentally-UPPERCASE types
    // that every case-sensitive client matcher misses; this test makes that impossible here.
    [Fact]
    public void Every_registered_type_is_snake_case()
    {
        var pattern = new Regex("^[a-z0-9_]+$");
        Assert.All(ErrorTypes.All, type => Assert.Matches(pattern, type));
    }

    [Fact]
    public void Registry_has_no_duplicates()
    {
        Assert.Equal(ErrorTypes.All.Count, ErrorTypes.All.Distinct().Count());
    }

    [Fact]
    public void Registry_covers_every_declared_constant()
    {
        var declared = typeof(ErrorTypes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        Assert.Equal(declared, ErrorTypes.All.ToHashSet());
    }
}

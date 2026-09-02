using Praxy.Core.Errors;
using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class QueryDslTests
{
    [Fact]
    public void Parses_a_simple_equal_query()
    {
        var parsed = QueryDsl.Parse([/* lang=json */ """{"method":"equal","attribute":"title","values":["Hello"]}"""]);
        Assert.Single(parsed);
        Assert.Equal("equal", parsed[0].Method);
        Assert.Equal("title", parsed[0].Attribute);
        Assert.Equal("Hello", parsed[0].Values[0].GetString());
    }

    [Fact]
    public void Parses_nested_and_or_queries()
    {
        var parsed = QueryDsl.Parse([
            /* lang=json */ """
            {"method":"and","values":[
              {"method":"equal","attribute":"a","values":[1]},
              {"method":"or","values":[
                {"method":"equal","attribute":"b","values":[2]},
                {"method":"equal","attribute":"c","values":[3]}
              ]}
            ]}
            """,
        ]);
        var and = parsed[0];
        Assert.Equal("and", and.Method);
        Assert.Equal(2, and.Children.Length);
        Assert.Equal("or", and.Children[1].Method);
        Assert.Equal(2, and.Children[1].Children.Length);
    }

    [Fact]
    public void More_than_max_queries_is_rejected()
    {
        var many = Enumerable.Range(0, QueryDsl.MaxQueries + 1)
            .Select(_ => /* lang=json */ """{"method":"isNull","attribute":"x"}""").ToArray();
        var ex = Assert.Throws<PraxyException>(() => QueryDsl.Parse(many));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void A_query_longer_than_the_char_cap_is_rejected()
    {
        var huge = $$"""{"method":"equal","attribute":"title","values":["{{new string('x', QueryDsl.MaxQueryChars)}}"]}""";
        Assert.Throws<PraxyException>(() => QueryDsl.Parse([huge]));
    }

    [Fact]
    public void Nesting_beyond_the_depth_cap_is_rejected()
    {
        // and > and > and > equal — one level past MaxDepth (3).
        var deep = /* lang=json */ """
            {"method":"and","values":[{"method":"and","values":[{"method":"and","values":[
              {"method":"equal","attribute":"x","values":[1]}
            ]}]}]}
            """;
        var ex = Assert.Throws<PraxyException>(() => QueryDsl.Parse([deep]));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void Unknown_method_is_rejected()
    {
        Assert.Throws<PraxyException>(() => QueryDsl.Parse(["""{"method":"regex","attribute":"x","values":["y"]}"""]));
    }

    [Fact]
    public void Malformed_json_is_rejected()
    {
        Assert.Throws<PraxyException>(() => QueryDsl.Parse(["not json"]));
    }

    [Theory]
    [InlineData("""{"method":"equal","values":["x"]}""")] // missing attribute
    [InlineData("""{"method":"between","attribute":"x","values":[1]}""")] // between needs exactly 2
    [InlineData("""{"method":"limit","values":[1,2]}""")] // limit needs exactly 1
    [InlineData("""{"method":"and","values":[]}""")] // and needs at least one nested query
    [InlineData("""{"method":"near","attribute":"loc","values":[1,2]}""")] // near needs exactly 3 (lat, lng, radius)
    [InlineData("""{"method":"near","attribute":"loc","values":[1,2,3,4]}""")]
    public void Malformed_shapes_are_rejected(string query) =>
        Assert.Throws<PraxyException>(() => QueryDsl.Parse([query]));

    [Fact]
    public void Near_with_exactly_three_values_parses()
    {
        var parsed = QueryDsl.Parse(["""{"method":"near","attribute":"loc","values":[37.7749,-122.4194,5000]}"""]);
        Assert.Equal("near", parsed[0].Method);
        Assert.Equal(3, parsed[0].Values.Length);
    }

    [Fact]
    public void IsNull_and_select_and_limit_parse_without_an_attribute_where_appropriate()
    {
        var parsed = QueryDsl.Parse([
            """{"method":"isNull","attribute":"a"}""",
            """{"method":"select","values":["a","b"]}""",
            """{"method":"limit","values":[10]}""",
        ]);
        Assert.Equal(3, parsed.Count);
        Assert.Equal("select", parsed[1].Method);
        Assert.Equal(2, parsed[1].Values.Length);
    }
}

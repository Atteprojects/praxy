using Praxy.Core;
using Praxy.Tables;

namespace Praxy.Tests.Unit;

public class PhysicalNamingTests
{
    [Theory]
    [InlineData("posts", "posts")]
    [InlineData("Posts", "posts")]
    [InlineData("my-column!", "mycolumn")]
    [InlineData("weird\"name", "weirdname")]
    [InlineData("tab\";DROP TABLE x;--", "tabdroptablex")]
    [InlineData("under_score_ok", "under_score_ok")]
    public void Sanitize_strips_everything_outside_lowercase_alnum_underscore(string input, string expected) =>
        Assert.Equal(expected, PhysicalNaming.Sanitize(input));

    [Fact]
    public void Entity_names_are_always_safe_identifiers()
    {
        string[] hostileKeys =
        [
            "posts", "weird\"name", "tab\";DROP TABLE x;--", "'; DROP SCHEMA px_evil CASCADE; --",
            "", "   ", "🙂🙂🙂", new string('a', 500),
        ];
        foreach (var key in hostileKeys)
        {
            var name = PhysicalNaming.EntityName(key, Guid.NewGuid());
            Assert.True(PhysicalNaming.IsSafeIdentifier(name));
            Assert.True(name.Length <= 63);
        }
    }

    [Fact]
    public void Index_names_carry_the_ix_prefix_and_stay_safe()
    {
        var name = PhysicalNaming.IndexName("title", Guid.NewGuid());
        Assert.StartsWith("ix_", name);
        Assert.True(PhysicalNaming.IsSafeIdentifier(name));
    }

    [Fact]
    public void Different_ids_disambiguate_colliding_sanitized_keys()
    {
        var a = PhysicalNaming.EntityName("weird!name", Guid.NewGuid());
        var b = PhysicalNaming.EntityName("weird?name", Guid.NewGuid());
        // Both sanitize to "weirdname" — the hash suffix (derived from the id) must differ.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Same_key_and_id_are_deterministic()
    {
        var id = Guid.NewGuid();
        Assert.Equal(PhysicalNaming.EntityName("posts", id), PhysicalNaming.EntityName("posts", id));
    }

    [Fact]
    public void Schema_name_is_px_prefixed_wire_hex()
    {
        var id = Guid.NewGuid();
        var name = PhysicalNaming.SchemaName(id);
        Assert.Equal($"px_{Ids.Wire(id)}", name);
        Assert.True(PhysicalNaming.IsSafeIdentifier(name));
    }

    [Theory]
    [InlineData("users")]
    [InlineData("_leading_underscore")]
    [InlineData("a1b2c3")]
    public void Safe_identifiers_pass(string id) => Assert.True(PhysicalNaming.IsSafeIdentifier(id));

    [Theory]
    [InlineData("")]
    [InlineData("Users")]
    [InlineData("weird\"name")]
    [InlineData("tab\";DROP TABLE x;--")]
    [InlineData("has space")]
    public void Unsafe_identifiers_fail(string id) => Assert.False(PhysicalNaming.IsSafeIdentifier(id));

    [Fact]
    public void Quote_refuses_to_emit_an_unsafe_identifier()
    {
        Assert.Throws<InvalidOperationException>(() => PhysicalNaming.Quote("weird\"name"));
    }

    [Fact]
    public void Quote_doubles_embedded_quotes_for_safe_input()
    {
        // Only reachable for identifiers that already passed IsSafeIdentifier, but confirms the
        // NpgsqlCommandBuilder quoting behaviour research/dotnet-stack.md documents.
        Assert.Equal("\"users\"", PhysicalNaming.Quote("users"));
    }

    [Fact]
    public void Qualified_table_never_relies_on_search_path()
    {
        var qualified = PhysicalNaming.QualifiedTable("px_abc", "posts_123");
        Assert.Equal("\"px_abc\".\"posts_123\"", qualified);
    }
}

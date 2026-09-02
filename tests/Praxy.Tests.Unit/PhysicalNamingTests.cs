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

    /// <summary>
    /// The same budget bug geo columns hit with their derived <c>_lng</c>/<c>_lat</c> aliases, flagged
    /// as pre-existing in docs/handoff/geo-nearby-phase-1-report.md and unfixed until now: a fulltext
    /// index's generated tsvector column appends <c>__fts</c> to the index's own physical name, but
    /// IndexName budgeted the full 63 characters for itself. Keys are valid up to 64 characters
    /// (<see cref="Keys.MaxLength"/>), so a long-keyed fulltext index produced a 68-character
    /// identifier that <see cref="PhysicalNaming.Quote"/> then refused — an
    /// <see cref="InvalidOperationException"/> ("this is a bug, not user input") surfacing as a 500 for
    /// an ordinary, valid create request.
    /// </summary>
    [Fact]
    public void A_max_length_fulltext_index_key_leaves_room_for_the_fts_column_suffix()
    {
        var key = new string('a', Keys.MaxLength);
        var id = Guid.NewGuid();
        Assert.True(Keys.IsValid(key));

        // The bug, still reachable through the unreserved overload — this is exactly what the old
        // IndexName(key, id) produced, and why the flag has to be passed at the fulltext call site.
        var unreserved = PhysicalNaming.FulltextColumnName(PhysicalNaming.IndexName(key, id));
        Assert.False(PhysicalNaming.IsSafeIdentifier(unreserved));
        Assert.Throws<InvalidOperationException>(() => PhysicalNaming.Quote(unreserved));

        var indexName = PhysicalNaming.IndexName(key, id, forFulltext: true);
        var ftsColumn = PhysicalNaming.FulltextColumnName(indexName);

        Assert.True(PhysicalNaming.IsSafeIdentifier(indexName), $"index name is {indexName.Length} chars");
        Assert.True(PhysicalNaming.IsSafeIdentifier(ftsColumn), $"fts column is {ftsColumn.Length} chars");
        PhysicalNaming.Quote(ftsColumn); // must not throw
    }

    /// <summary>
    /// Third instance of the same derived-suffix budget bug, and the widest: a table's physical name
    /// grows a <c>__perms</c> row-permissions side table, and that side table in turn grows a
    /// <c>_action_role_idx</c> index — both built by string concatenation in
    /// <c>TablesService.SetRowSecurityAsync</c>. A long-keyed table therefore couldn't have row
    /// security enabled at all: the derived names ran to 70 and 86 characters against Postgres's
    /// 63-character limit.
    /// </summary>
    [Fact]
    public void A_max_length_table_key_leaves_room_for_the_perms_table_and_its_index()
    {
        var key = new string('a', Keys.MaxLength);
        Assert.True(Keys.IsValid(key));

        var tableName = PhysicalNaming.EntityName(key, Guid.NewGuid(), PhysicalNaming.RowSecuritySuffixChars);
        var permsTable = PhysicalNaming.PermsTableName(tableName);
        var permsIndex = $"{permsTable}_action_role_idx";

        Assert.True(PhysicalNaming.IsSafeIdentifier(tableName), $"table is {tableName.Length} chars");
        Assert.True(PhysicalNaming.IsSafeIdentifier(permsTable), $"perms table is {permsTable.Length} chars");
        Assert.True(PhysicalNaming.IsSafeIdentifier(permsIndex), $"perms index is {permsIndex.Length} chars");
        PhysicalNaming.Quote(permsIndex); // must not throw

        // The unreserved form is what shipped before this fix — both derived names blew the limit.
        var unreserved = PhysicalNaming.PermsTableName(PhysicalNaming.EntityName(key, Guid.NewGuid()));
        Assert.False(PhysicalNaming.IsSafeIdentifier(unreserved));
        Assert.False(PhysicalNaming.IsSafeIdentifier($"{unreserved}_action_role_idx"));
    }

    /// <summary>Non-fulltext indexes keep the full budget — their names gain no derived suffix.</summary>
    [Fact]
    public void A_non_fulltext_index_does_not_pay_the_fts_reservation()
    {
        var key = new string('a', Keys.MaxLength);
        var id = Guid.NewGuid();
        Assert.True(PhysicalNaming.IndexName(key, id).Length > PhysicalNaming.IndexName(key, id, forFulltext: true).Length);
        Assert.True(PhysicalNaming.IsSafeIdentifier(PhysicalNaming.IndexName(key, id)));
    }
}

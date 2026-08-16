using Praxy.Core.Errors;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Tests.Unit;

public class QueryCompilerTests
{
    private static CatalogEntry BuildEntry(
        bool rowSecurity = false, TablePermission[]? permissions = null, IndexDef[]? indexes = null)
    {
        var databaseId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var database = new Database
        { Id = databaseId, ProjectId = "proj1", Key = "db", Name = "DB", SchemaName = "px_test" };
        var table = new TableDef
        { Id = tableId, DatabaseId = databaseId, Key = "posts", Name = "Posts", PhysicalName = "posts_abc123", RowSecurity = rowSecurity };
        var columns = new List<ColumnDef>
        {
            new() { Id = Guid.NewGuid(), TableId = tableId, Key = "title", Type = ColumnTypes.String, PhysicalName = "title_x1", Size = 200 },
            new() { Id = Guid.NewGuid(), TableId = tableId, Key = "views", Type = ColumnTypes.Integer, PhysicalName = "views_x1" },
        };
        return new CatalogEntry(database, table, columns, indexes ?? [], permissions ?? []);
    }

    private static List<ParsedQuery> Q(params string[] raw) => QueryDsl.Parse(raw);

    [Fact]
    public void No_grants_and_row_security_off_denies_everyone()
    {
        var entry = BuildEntry();
        var compiled = QueryCompiler.CompileList(entry, [], ["any"], bypassPermissions: false, includeTotal: false);
        Assert.Contains("FALSE", compiled.Sql);
    }

    [Fact]
    public void A_matching_table_level_grant_short_circuits_to_true()
    {
        var entry = BuildEntry(permissions: [new TablePermission { TableId = Guid.NewGuid(), Action = "read", Role = "any" }]);
        var compiled = QueryCompiler.CompileList(entry, [], ["any"], bypassPermissions: false, includeTotal: false);
        Assert.Contains("TRUE", compiled.Sql);
        Assert.DoesNotContain("__perms", compiled.Sql);
    }

    [Fact]
    public void Row_security_on_without_a_table_grant_adds_an_exists_against_perms()
    {
        var entry = BuildEntry(rowSecurity: true);
        var compiled = QueryCompiler.CompileList(entry, [], ["any"], bypassPermissions: false, includeTotal: false);
        Assert.Contains("EXISTS", compiled.Sql);
        Assert.Contains("__perms", compiled.Sql);
    }

    [Fact]
    public void Bypass_permissions_always_yields_true_even_with_no_grants()
    {
        var entry = BuildEntry();
        var compiled = QueryCompiler.CompileList(entry, [], [], bypassPermissions: true, includeTotal: false);
        Assert.Contains("TRUE", compiled.Sql);
    }

    [Fact]
    public void Unknown_attribute_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"equal","attribute":"nope","values":["x"]}""");
        Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
    }

    [Fact]
    public void Search_without_a_fulltext_index_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"search","attribute":"title","values":["hello"]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void Search_with_an_available_fulltext_index_compiles()
    {
        var index = new IndexDef
        {
            Id = Guid.NewGuid(), TableId = Guid.NewGuid(), Key = "idx", Type = IndexesService.TypeFulltext,
            Columns = ["title"], PhysicalName = "ix_title_x1", Status = "available",
        };
        var entry = BuildEntry(indexes: [index]);
        var queries = Q("""{"method":"search","attribute":"title","values":["hello"]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("websearch_to_tsquery", compiled.Sql);
    }

    [Fact]
    public void Limit_over_the_cap_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q($$"""{"method":"limit","values":[{{QueryDsl.MaxLimit + 1}}]}""");
        Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
    }

    [Fact]
    public void Offset_and_cursor_together_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"offset","values":[10]}""", """{"method":"cursorAfter","values":["0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4"]}""");
        Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
    }

    [Fact]
    public void Both_cursors_together_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q(
            """{"method":"cursorAfter","values":["0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4"]}""",
            """{"method":"cursorBefore","values":["0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4"]}""");
        Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
    }

    [Fact]
    public void Equal_filter_binds_a_parameterized_array_never_the_raw_value()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"equal","attribute":"title","values":["Robert'); DROP TABLE x;--"]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.DoesNotContain("DROP TABLE", compiled.Sql);
        Assert.Contains(compiled.Params, p => p.Value is string[] values && values.Contains("Robert'); DROP TABLE x;--"));
    }

    [Fact]
    public void Select_restricts_the_returned_columns()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"select","values":["title"]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Equal(["title"], compiled.SelectedKeys!);
        Assert.Contains("title_x1", compiled.Sql);
        Assert.DoesNotContain("views_x1", compiled.Sql);
    }

    [Fact]
    public void Permission_predicate_for_a_single_row_matches_the_list_compilers_logic()
    {
        var entry = BuildEntry(permissions: [new TablePermission { TableId = Guid.NewGuid(), Action = "delete", Role = "users" }]);
        var predicate = QueryCompiler.CompilePermissionPredicate(entry, "delete", ["users"], bypassPermissions: false);
        Assert.Equal("TRUE", predicate.Sql);
    }
}

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
            new()
            {
                Id = Guid.NewGuid(), TableId = tableId, Key = "authorId", Type = ColumnTypes.Relationship,
                PhysicalName = "author_id_x1", TargetTableId = Guid.NewGuid(),
            },
            new() { Id = Guid.NewGuid(), TableId = tableId, Key = "loc", Type = ColumnTypes.Geo, PhysicalName = "loc_x1" },
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

    /// <summary>
    /// Found by Phase 9's query-compiler fuzz test: a filter value that doesn't match its column's
    /// type (a string against an integer column here) used to reach Postgres as a bad parameter and
    /// come back as an unhandled 500 — <see cref="QueryCompiler"/>'s value conversion now catches
    /// that <see cref="FormatException"/> and rethrows it as the same 400 every other malformed-query
    /// shape in this compiler already produces, never a raw exception escaping to the caller.
    /// </summary>
    [Fact]
    public void A_filter_value_that_does_not_match_its_columns_type_is_a_clean_400_not_a_crash()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"equal","attribute":"views","values":["not-a-number"]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(400, ex.Code);
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void Equal_isNull_and_isNotNull_compile_for_a_relationship_column()
    {
        var entry = BuildEntry();
        var wireId = "0195a1b2c3d4e5f6a7b8c9d0e1f2a3b4";
        var equal = QueryCompiler.CompileList(entry, Q($$"""{"method":"equal","attribute":"authorId","values":["{{wireId}}"]}"""), ["any"], true, false);
        Assert.Contains("author_id_x1", equal.Sql);

        var isNull = QueryCompiler.CompileList(entry, Q("""{"method":"isNull","attribute":"authorId"}"""), ["any"], true, false);
        Assert.Contains("IS NULL", isNull.Sql);
    }

    [Fact]
    public void A_relationship_value_that_is_not_a_valid_id_is_a_clean_400_not_a_crash()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"equal","attribute":"authorId","values":["not-an-id"]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    /// <summary>'search' makes no sense on a uuid — rejected up front, same as $id/datetime/numeric/boolean.</summary>
    [Fact]
    public void Search_against_a_relationship_column_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"search","attribute":"authorId","values":["hello"]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    private static IndexDef SpatialIndex() => new()
    {
        Id = Guid.NewGuid(), TableId = Guid.NewGuid(), Key = "idx_loc", Type = IndexesService.TypeSpatial,
        Columns = ["loc"], PhysicalName = "ix_loc_x1", Status = "available",
    };

    [Fact]
    public void OrderNear_on_a_non_geo_column_is_rejected()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"orderNear","attribute":"title","values":[37.7749,-122.4194]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void OrderNear_on_a_geo_column_with_no_spatial_index_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    /// <summary>
    /// Ordering compiles to the GiST-index-accelerated &lt;-&gt; KNN operator — the only form Postgres
    /// can use the spatial index to order by. $distance is spelled out separately as ST_Distance's
    /// explicit *sphere* variant, which is numerically identical to &lt;-&gt; (docs/research/
    /// geo-nearby.md's "Distance model"), so ordering and displayed value can never contradict.
    /// </summary>
    [Fact]
    public void OrderNear_orders_by_the_knn_operator_and_returns_distance_as_sphere_ST_Distance()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("loc_x1", compiled.Sql);
        Assert.Contains("ST_MakePoint", compiled.Sql);
        Assert.Contains("::geography", compiled.Sql);
        // The ORDER BY must use <->, or the spatial index can't accelerate it at all.
        Assert.Contains("<-> ST_MakePoint", compiled.Sql);
        Assert.Matches(@"ORDER BY t\.""loc_x1"" <->", compiled.Sql);
        // $distance must be the *sphere* variant — the `false` third argument is what makes it agree
        // with <->. Without it ST_Distance defaults to the spheroid and the two disagree by metres.
        Assert.Contains(", false) AS \"$distance\"", compiled.Sql);
    }

    /// <summary>
    /// The design doc's landmine: AddParam must be called exactly once each for lat/lng, with the
    /// same "@pN" name reused verbatim across the ORDER BY (no cursor here, so just one site) —
    /// never a duplicate parameter for the same logical value.
    /// </summary>
    [Fact]
    public void OrderNear_adds_exactly_one_param_each_for_lat_and_lng()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Equal(1, compiled.Params.Count(p => p.Value is double d && d == 37.7749));
        Assert.Equal(1, compiled.Params.Count(p => p.Value is double d && d == -122.4194));
    }

    [Fact]
    public void OrderNear_composes_with_a_near_radius_filter()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q(
            """{"method":"near","attribute":"loc","values":[37.7749,-122.4194,5000]}""",
            """{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("ST_DWithin", compiled.Sql);
        Assert.Contains("<->", compiled.Sql);
    }

    /// <summary>First order method sent wins — same rule across orderAsc/orderDesc/orderNear.</summary>
    [Fact]
    public void First_order_method_wins_when_orderAsc_precedes_orderNear()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q(
            """{"method":"orderAsc","attribute":"title"}""",
            """{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("title_x1", compiled.Sql);
        Assert.DoesNotContain("<->", compiled.Sql);
        Assert.DoesNotContain("$distance", compiled.Sql);
        Assert.False(compiled.HasDistance);
    }

    [Fact]
    public void OrderNear_standalone_with_no_near_filter_still_compiles()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.DoesNotContain("ST_DWithin", compiled.Sql);
        Assert.Contains("<->", compiled.Sql);
    }

    /// <summary>
    /// The shared <c>RequireNearValue</c> serves both methods, so its message has to name the one the
    /// caller actually sent — an 'orderNear' failure reporting itself as 'near' sends people looking
    /// at the wrong query.
    /// </summary>
    [Theory]
    [InlineData("""{"method":"orderNear","attribute":"loc","values":["nope",-122.4194]}""", "orderNear", "lat")]
    [InlineData("""{"method":"orderNear","attribute":"loc","values":[37.7749,"nope"]}""", "orderNear", "lng")]
    [InlineData("""{"method":"near","attribute":"loc","values":["nope",-122.4194,500]}""", "near", "lat")]
    [InlineData("""{"method":"near","attribute":"loc","values":[37.7749,-122.4194,"nope"]}""", "near", "radiusMeters")]
    public void A_non_numeric_query_point_value_names_the_method_the_caller_sent(
        string query, string method, string label)
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var ex = Assert.Throws<PraxyException>(
            () => QueryCompiler.CompileList(entry, Q(query), ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
        Assert.Equal($"'{method}' requires numeric values.", ex.Message);
        Assert.Contains($"'{method}' {label} must be a number.", ex.Fields!["queries"]);
    }

    // ---- $distance (Phase 3, docs/handoff/geo-nearby-phase-3-prompt.md) --------------------------

    [Fact]
    public void OrderNear_marks_HasDistance_and_appends_a_trailing_distance_column()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.True(compiled.HasDistance);
        Assert.Contains("AS \"$distance\"", compiled.Sql);
        // Appended at the very end of the select list — the ordinal-safety rule BuildRowJson relies on.
        Assert.True(compiled.Sql.IndexOf("AS \"$distance\"", StringComparison.Ordinal) >
                     compiled.Sql.IndexOf("loc_x1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bare_near_filter_has_no_distance()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"near","attribute":"loc","values":[37.7749,-122.4194,5000]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.False(compiled.HasDistance);
        Assert.DoesNotContain("$distance", compiled.Sql);
    }

    [Fact]
    public void A_plain_unsorted_list_has_no_distance()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var compiled = QueryCompiler.CompileList(entry, [], ["any"], true, false);
        Assert.False(compiled.HasDistance);
        Assert.DoesNotContain("$distance", compiled.Sql);
    }

    [Fact]
    public void Distance_survives_select_narrowing()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q(
            """{"method":"select","values":["title"]}""",
            """{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.True(compiled.HasDistance);
        Assert.Contains("AS \"$distance\"", compiled.Sql);
        Assert.Equal(["title"], compiled.SelectedKeys!);
    }

    /// <summary>
    /// The whole geo surface must stay on ONE distance model, or a row can display a distance that
    /// contradicts the radius that selected it (verified against real PostGIS: a point at
    /// sphere-3002.267m / spheroid-2996.797m is inside a spheroid <c>near(...,3000)</c> while
    /// displaying as 3002m). Sphere is the model, because it's the only one <c>&lt;-&gt;</c> can order
    /// by using the spatial index. That means <c>near</c>'s ST_DWithin and <c>$distance</c>'s
    /// ST_Distance both need their explicit <c>false</c> argument — the default for both is spheroid,
    /// so dropping it is a silent, plausible-looking regression this test exists to catch.
    /// </summary>
    [Fact]
    public void Near_and_distance_both_pin_the_sphere_model_explicitly()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q(
            """{"method":"near","attribute":"loc","values":[37.7749,-122.4194,3000]}""",
            """{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Matches(@"ST_DWithin\(.*?\)::geography, @p\d+, false\)", compiled.Sql);
        Assert.Matches(@"ST_Distance\(.*?\)::geography, false\) AS ""\$distance""", compiled.Sql);
        Assert.DoesNotContain("ST_Distance(t.\"loc_x1\", ST_MakePoint(@p0, @p1)::geography)", compiled.Sql);
    }


    // ---- withinBox (Phase 3) ----------------------------------------------------------------------

    [Fact]
    public void WithinBox_on_a_non_geo_column_is_rejected()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"withinBox","attribute":"title","values":[37.7,-122.5,37.8,-122.4]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void WithinBox_on_a_geo_column_with_no_spatial_index_is_rejected()
    {
        var entry = BuildEntry();
        var queries = Q("""{"method":"withinBox","attribute":"loc","values":[37.7,-122.5,37.8,-122.4]}""");
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    /// <summary>
    /// Wire values are lat-first (minLat, minLng, maxLat, maxLng); ST_MakeEnvelope takes x/y
    /// (lng, lat) pairs. The compiler must reorder them — checked by asserting the actual bound
    /// parameter values in call order, not just that the SQL text contains the right function names.
    /// </summary>
    [Fact]
    public void WithinBox_reorders_lat_first_wire_values_into_st_makeenvelopes_lng_lat_order()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"withinBox","attribute":"loc","values":[10,20,30,40]}"""); // minLat,minLng,maxLat,maxLng
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("ST_Intersects", compiled.Sql);
        Assert.Contains("ST_MakeEnvelope", compiled.Sql);
        Assert.Contains("::geography", compiled.Sql);

        var doubleParams = compiled.Params.Where(p => p.Value is double).Select(p => (double)p.Value).ToList();
        Assert.Equal([20.0, 10.0, 40.0, 30.0], doubleParams); // minLng, minLat, maxLng, maxLat
    }

    [Fact]
    public void WithinBox_rejects_minLat_greater_or_equal_to_maxLat()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"withinBox","attribute":"loc","values":[37.8,-122.5,37.7,-122.4]}"""); // minLat > maxLat
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void WithinBox_rejects_a_box_crossing_the_antimeridian()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q("""{"method":"withinBox","attribute":"loc","values":[37.7,170,37.8,-170]}"""); // minLng > maxLng
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, queries, ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }

    [Fact]
    public void WithinBox_composes_with_orderNear()
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var queries = Q(
            """{"method":"withinBox","attribute":"loc","values":[37.7,-122.5,37.8,-122.4]}""",
            """{"method":"orderNear","attribute":"loc","values":[37.7749,-122.4194]}""");
        var compiled = QueryCompiler.CompileList(entry, queries, ["any"], true, false);
        Assert.Contains("ST_Intersects", compiled.Sql);
        Assert.True(compiled.HasDistance);
    }

    // ---- coordinate-range validation (Phase 3) — consistent across near/orderNear/withinBox ------

    [Theory]
    [InlineData("""{"method":"near","attribute":"loc","values":[200,-122.4194,5000]}""")] // lat > 90
    [InlineData("""{"method":"near","attribute":"loc","values":[37.7749,-200,5000]}""")] // lng < -180
    [InlineData("""{"method":"orderNear","attribute":"loc","values":[-91,-122.4194]}""")]
    [InlineData("""{"method":"orderNear","attribute":"loc","values":[37.7749,181]}""")]
    [InlineData("""{"method":"withinBox","attribute":"loc","values":[91,-122.5,37.8,-122.4]}""")]
    [InlineData("""{"method":"withinBox","attribute":"loc","values":[37.7,-181,37.8,-122.4]}""")]
    [InlineData("""{"method":"withinBox","attribute":"loc","values":[37.7,-122.5,91,-122.4]}""")]
    [InlineData("""{"method":"withinBox","attribute":"loc","values":[37.7,-122.5,37.8,181]}""")]
    public void Out_of_range_coordinates_are_rejected_for_near_orderNear_and_withinBox(string query)
    {
        var entry = BuildEntry(indexes: [SpatialIndex()]);
        var ex = Assert.Throws<PraxyException>(() => QueryCompiler.CompileList(entry, Q(query), ["any"], true, false));
        Assert.Equal(ErrorTypes.GeneralQueryInvalid, ex.Type);
    }
}

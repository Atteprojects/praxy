using System.Text.Json;
using System.Text.Json.Nodes;
using Praxy.Core;
using Praxy.Persistence.Entities;
using Database = Praxy.Persistence.Entities.Database;

namespace Praxy.Api.Endpoints;

// ---- requests -------------------------------------------------------------------------------

public sealed record CreateDatabaseRequest(string Key, string Name);

public sealed record CreateTableRequest(string Key, string Name);

public sealed record UpdateTableRequest(string? Name, bool? Enabled);

public sealed record CreateColumnRequest(
    string Key, bool? Required, bool? Array, JsonElement? Default, int? Size, string[]? Elements);

public sealed record UpdateColumnRequest(string? Key, bool? Required);

public sealed record CreateIndexRequest(string Key, string Type, string[] Columns, string[]? Orders);

public sealed record UpdateTablePermissionsRequest(bool? RowSecurity, string[]? Permissions);

// ---- responses ------------------------------------------------------------------------------

public sealed record DatabaseResponse(string Id, string Key, string Name, DateTimeOffset CreatedAt)
{
    public static DatabaseResponse From(Database d) => new(Ids.Wire(d.Id), d.Key, d.Name, d.CreatedAt);
}

public sealed record TableResponse(
    string Id, string DatabaseId, string Key, string Name, bool RowSecurity, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TableResponse From(TableDef t) => new(
        Ids.Wire(t.Id), Ids.Wire(t.DatabaseId), t.Key, t.Name, t.RowSecurity, t.Enabled, t.CreatedAt, t.UpdatedAt);
}

public sealed record ColumnResponse(
    string Id, string TableId, string Key, string Type, bool Required, bool Array, int? Size,
    JsonNode? Default, string[]? Elements, string Status, string? Error, int Position,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ColumnResponse From(ColumnDef c) => new(
        Ids.Wire(c.Id), Ids.Wire(c.TableId), c.Key, c.Type, c.Required, c.IsArray, c.Size,
        ParseDefault(c.DefaultValue), Praxy.Tables.ColumnTypes.ExtractElements(c.Options), c.Status, c.Error, c.Position,
        c.CreatedAt, c.UpdatedAt);

    private static JsonNode? ParseDefault(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record IndexResponse(
    string Id, string TableId, string Key, string Type, string[] Columns, string[] Orders,
    string Status, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static IndexResponse From(IndexDef i) => new(
        Ids.Wire(i.Id), Ids.Wire(i.TableId), i.Key, i.Type, i.Columns, i.Orders, i.Status, i.Error,
        i.CreatedAt, i.UpdatedAt);
}

public sealed record TablePermissionsResponse(bool RowSecurity, string[] Permissions);

public sealed record SchemaJobResponse(
    string Id, string DatabaseId, string? TableId, string? IndexId, string Kind, string Status, int Attempts,
    string? Error, DateTimeOffset StartedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SchemaJobResponse From(SchemaJob j) => new(
        Ids.Wire(j.Id), Ids.Wire(j.DatabaseId), j.TableId is { } t ? Ids.Wire(t) : null, ExtractIndexId(j),
        j.Kind, j.Status, j.Attempts, j.Error, j.UpdatedAt, j.CreatedAt, j.UpdatedAt);

    private static string? ExtractIndexId(SchemaJob j)
    {
        if (j.Kind != Praxy.Tables.SchemaJobKinds.CreateIndex)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Praxy.Tables.CreateIndexJobPayload>(j.Payload)?.IndexId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

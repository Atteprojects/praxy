using System.Text.Json;
using System.Text.Json.Nodes;

namespace Praxy.Api.Endpoints;

public sealed record CreateRowRequest(string? RowId, JsonElement Data, string[]? Permissions);

public sealed record UpdateRowRequest(JsonElement? Data, string[]? Permissions);

public sealed record RowListResponse(int? Total, IReadOnlyList<JsonObject> Rows);

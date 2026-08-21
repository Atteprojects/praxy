using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxy.Tables.Quotas;

/// <summary>
/// The parsed shape of <see cref="Praxy.Persistence.Entities.Organization.Limits"/> — every field is
/// nullable because an unset dimension means "use the instance-wide default from
/// <see cref="QuotaOptions"/>", not zero. A self-hoster who never touches an org's <c>limits</c> jsonb
/// (every org today, since orgs are hidden in the console — architecture.md §11) sees no behavior
/// change: <c>{}</c> parses to all-null, and every dimension falls through to the same numbers this
/// engine has enforced since Phase 2. Same tolerant-parse shape as
/// <see cref="Praxy.Messaging.EmailProviderConfig"/>.
/// </summary>
public sealed record OrganizationLimits(
    [property: JsonPropertyName("maxProjects")] int? MaxProjects = null,
    [property: JsonPropertyName("maxDatabasesPerProject")] int? MaxDatabasesPerProject = null,
    [property: JsonPropertyName("maxTablesPerDatabase")] int? MaxTablesPerDatabase = null,
    [property: JsonPropertyName("maxColumnsPerTable")] int? MaxColumnsPerTable = null,
    [property: JsonPropertyName("maxIndexesPerTable")] int? MaxIndexesPerTable = null,
    [property: JsonPropertyName("maxSitesPerProject")] int? MaxSitesPerProject = null)
{
    public static readonly OrganizationLimits Unlimited = new();

    public static OrganizationLimits Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Unlimited;
        try
        {
            return JsonSerializer.Deserialize<OrganizationLimits>(json) ?? Unlimited;
        }
        catch (JsonException)
        {
            return Unlimited;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

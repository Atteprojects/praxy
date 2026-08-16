using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

/// <summary>The scopes an API key can hold. Grows per phase; validated at creation.</summary>
public static class ApiKeyScopes
{
    public const string UsersRead = "users.read";
    public const string UsersWrite = "users.write";
    public const string TeamsRead = "teams.read";
    public const string TeamsWrite = "teams.write";
    public const string DatabasesRead = "databases.read";
    public const string DatabasesWrite = "databases.write";
    public const string FunctionsExecute = "functions.execute";

    public static readonly IReadOnlyList<string> All =
        [UsersRead, UsersWrite, TeamsRead, TeamsWrite, DatabasesRead, DatabasesWrite, FunctionsExecute];
}

/// <summary>
/// Server-side API keys: <c>&lt;keyId&gt;.&lt;secret&gt;</c> in <c>X-Praxy-Key</c>, SHA-256 at
/// rest, scoped, project-bound. The reserved console project is refused here *and* by a DB
/// check constraint — belt and braces.
/// </summary>
public sealed class ApiKeyService(PraxyDb db)
{
    /// <summary>Stamp <c>last_used_at</c> at most this often per key — it is telemetry, not an audit log.</summary>
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromSeconds(60);

    public async Task<(ApiKey Key, string Secret)> CreateAsync(
        string projectId, string name, string[] scopes, DateTimeOffset? expiresAt,
        bool bypassRowPermissions = false, CancellationToken ct = default)
    {
        if (Ids.IsReservedProjectId(projectId))
            throw new PraxyException(403, ErrorTypes.ProjectReserved,
                "The console project can never hold API keys.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            throw PraxyException.ArgumentInvalid("Invalid key payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });

        var unknown = scopes.Where(s => !ApiKeyScopes.All.Contains(s)).ToArray();
        if (scopes.Length == 0 || unknown.Length > 0)
            throw PraxyException.ArgumentInvalid("Invalid key scopes.",
                new Dictionary<string, string[]>
                {
                    ["scopes"] = [unknown.Length > 0
                        ? $"Unknown scopes: {string.Join(", ", unknown)}."
                        : $"Provide at least one of: {string.Join(", ", ApiKeyScopes.All)}."],
                });

        var id = Ids.NewUuid();
        var (secret, secretHash) = SessionTokens.Create(id);
        var apiKey = new ApiKey
        {
            Id = id,
            ProjectId = projectId,
            Name = name.Trim(),
            SecretHash = secretHash,
            Scopes = scopes.Distinct().ToArray(),
            ExpiresAt = expiresAt,
            BypassRowPermissions = bypassRowPermissions,
        };
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(ct);
        return (apiKey, secret);
    }

    /// <summary>Resolves an <c>X-Praxy-Key</c> value. Null when invalid, expired, or from another project.</summary>
    public async Task<ApiKey?> ResolveAsync(string projectId, string keyToken, CancellationToken ct = default)
    {
        if (!SessionTokens.TryParse(keyToken, out var keyId, out var secretHash))
            return null;

        var apiKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (apiKey is null || !SessionTokens.HashEquals(apiKey.SecretHash, secretHash))
            return null;
        if (apiKey.ProjectId != projectId)
            return null;
        if (apiKey.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (apiKey.LastUsedAt is null || now - apiKey.LastUsedAt > LastUsedResolution)
        {
            await db.ApiKeys.Where(k => k.Id == apiKey.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
            apiKey.LastUsedAt = now;
        }
        return apiKey;
    }
}

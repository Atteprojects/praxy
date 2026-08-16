using Microsoft.EntityFrameworkCore;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Messaging;

/// <summary>
/// Console-facing CRUD for messaging providers. Only <c>email</c> ships this phase (validated
/// against <see cref="KnownTypes"/>) — the shape (type discriminator + non-secret jsonb config +
/// separately encrypted secret) is what lets SMS/push slot in later without a rewrite, per the
/// roadmap's "model providers generically now".
/// </summary>
public sealed class MessagingProvidersService(PraxyDb db, InstanceKey key)
{
    public static readonly IReadOnlyList<string> KnownTypes = ["email"];

    public Task<List<MessagingProvider>> ListAsync(string projectId, CancellationToken ct) =>
        db.MessagingProviders.Where(p => p.ProjectId == projectId).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);

    public async Task<MessagingProvider> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.MessagingProviders.FirstOrDefaultAsync(p => p.Id == id && p.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.MessagingProviderNotFound, "Provider not found.");

    public async Task<MessagingProvider> CreateAsync(
        string projectId, string type, string name, EmailProviderConfig config, string? secret,
        bool isDefault, CancellationToken ct)
    {
        var fields = Validate(type, name, config);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid provider payload.", fields);

        // The project's first provider of a type is the obvious default; later ones need an explicit flip.
        var isFirstOfType = !await db.MessagingProviders.AnyAsync(p => p.ProjectId == projectId && p.Type == type, ct);
        var provider = new MessagingProvider
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Type = type,
            Name = name.Trim(),
            Config = config.ToJson(),
            ProtectedSecret = secret is { Length: > 0 } ? key.Encrypt(secret) : null,
            IsDefault = isDefault || isFirstOfType,
        };
        if (provider.IsDefault)
            await ClearDefaultAsync(projectId, type, ct);
        db.MessagingProviders.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }

    public async Task<MessagingProvider> UpdateAsync(
        MessagingProvider provider, string? name, EmailProviderConfig? config, string? secret, bool clearSecret,
        bool? enabled, bool? isDefault, CancellationToken ct)
    {
        var fields = Validate(provider.Type, name ?? provider.Name, config ?? EmailProviderConfig.Parse(provider.Config));
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid provider payload.", fields);

        if (name is not null)
            provider.Name = name.Trim();
        if (config is not null)
            provider.Config = config.ToJson();
        if (secret is { Length: > 0 })
            provider.ProtectedSecret = key.Encrypt(secret);
        else if (clearSecret)
            provider.ProtectedSecret = null;
        if (enabled is { } en)
            provider.Enabled = en;
        if (isDefault is true)
        {
            await ClearDefaultAsync(provider.ProjectId, provider.Type, ct);
            provider.IsDefault = true;
        }
        else if (isDefault is false)
        {
            provider.IsDefault = false;
        }
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return provider;
    }

    public async Task DeleteAsync(MessagingProvider provider, CancellationToken ct)
    {
        db.MessagingProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
    }

    private Task ClearDefaultAsync(string projectId, string type, CancellationToken ct) =>
        db.MessagingProviders
            .Where(p => p.ProjectId == projectId && p.Type == type && p.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);

    private static Dictionary<string, string[]> Validate(string type, string name, EmailProviderConfig config)
    {
        var fields = new Dictionary<string, string[]>();
        if (!KnownTypes.Contains(type))
            fields["type"] = [$"Must be one of: {string.Join(", ", KnownTypes)}."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        if (string.IsNullOrWhiteSpace(config.Host) || config.Host.Length > 256)
            fields["host"] = ["Must be between 1 and 256 characters."];
        if (config.Port is < 1 or > 65535)
            fields["port"] = ["Must be between 1 and 65535."];
        if (string.IsNullOrWhiteSpace(config.From) || !config.From.Contains('@') || config.From.Length > 320)
            fields["from"] = ["Must be a valid email address."];
        return fields;
    }
}

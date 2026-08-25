using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Sites;

/// <summary>
/// Console-facing CRUD for sites, their env vars, and deployments — metadata only, same split
/// <c>FunctionsService</c> draws from <c>FunctionBuildWorker</c>. The one place this differs from
/// Functions: <see cref="ActivateAsync"/> actually starts a container (a site's active deployment is
/// meant to be continuously running, not acquired lazily on first invocation), so it lives here
/// rather than purely in the build worker — both the worker's auto-activate-on-success path and the
/// console's explicit rollback endpoint call the same method.
/// </summary>
public sealed partial class SitesService(
    PraxyDb db, InstanceKey key, SiteDockerExecutor docker, SiteContainerRegistry registry,
    SiteBuildSignal buildSignal, SitesOptions options)
{
    // ---- sites ------------------------------------------------------------------------------------

    public Task<List<Site>> ListAsync(string projectId, CancellationToken ct) =>
        db.Sites.Where(s => s.ProjectId == projectId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);

    public async Task<Site> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.Sites.FirstOrDefaultAsync(s => s.Id == id && s.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.SiteNotFound, "Site not found.");

    public bool IsRunning(Site site) => site.ActiveDeploymentId is { } id && registry.TryGet(id, out _);

    public async Task<Site> CreateAsync(string projectId, string key_, string name, string rootDirectory, CancellationToken ct)
    {
        var fields = Validate(key_, name, rootDirectory);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid site payload.", fields);

        var site = new Site
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Key = key_.Trim(),
            Name = name.Trim(),
            RootDirectory = rootDirectory.Trim(),
        };
        db.Sites.Add(site);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.SiteAlreadyExists,
                $"A site with key '{key_}' already exists in this project.");
        }
        return site;
    }

    public async Task<Site> UpdateAsync(Site site, string? name, string? rootDirectory, bool? enabled, CancellationToken ct)
    {
        var fields = Validate(site.Key, name ?? site.Name, rootDirectory ?? site.RootDirectory);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid site payload.", fields);

        if (name is not null)
            site.Name = name.Trim();
        if (rootDirectory is not null)
            site.RootDirectory = rootDirectory.Trim();
        if (enabled is { } e)
            site.Enabled = e;
        site.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return site;
    }

    public async Task DeleteAsync(Site site, CancellationToken ct)
    {
        // Not just the active deployment — Phase 2 previews mean any of this site's deployments
        // may have a live (on-demand-started) container tracked in the registry right now.
        var deploymentIds = await db.SiteDeployments
            .Where(d => d.SiteId == site.Id).Select(d => d.Id).ToListAsync(ct);
        foreach (var deploymentId in deploymentIds)
            if (registry.TryRemove(deploymentId, out var running))
                await docker.StopAndRemoveAsync(running.ContainerId, CancellationToken.None);

        db.Sites.Remove(site);
        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, string[]> Validate(string key_, string name, string rootDirectory)
    {
        var fields = new Dictionary<string, string[]>();
        if (!Ids.IsValidCustomId(key_))
            fields["key"] = ["1-36 chars, lowercase alphanumeric or hyphen, must start alphanumeric."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        if (!SiteRuntimeTemplates.IsValidRootDirectory(rootDirectory.Trim()))
            fields["rootDirectory"] = ["Must be empty, or a relative path with no '..' segments."];
        return fields;
    }

    // ---- env vars -----------------------------------------------------------------------------

    public Task<List<SiteEnvVar>> ListEnvVarsAsync(Guid siteId, CancellationToken ct) =>
        db.SiteEnvVars.Where(v => v.SiteId == siteId).OrderBy(v => v.Key).ToListAsync(ct);

    public async Task<SiteEnvVar> SetEnvVarAsync(Guid siteId, string envKey, string value, CancellationToken ct)
    {
        var validKey = !string.IsNullOrWhiteSpace(envKey) && envKey.Length <= 256
            && (char.IsAsciiLetter(envKey[0]) || envKey[0] == '_')
            && envKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!validKey)
            throw PraxyException.ArgumentInvalid("Invalid environment variable key.",
                new Dictionary<string, string[]> { ["key"] = ["1-256 chars: letters, digits, underscore; must not start with a digit (used as a Docker build ARG name)."] });

        var existing = await db.SiteEnvVars.FirstOrDefaultAsync(v => v.SiteId == siteId && v.Key == envKey, ct);
        if (existing is not null)
        {
            existing.ProtectedValue = key.Encrypt(value);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var created = new SiteEnvVar
        {
            Id = Ids.NewUuid(),
            SiteId = siteId,
            Key = envKey,
            ProtectedValue = key.Encrypt(value),
        };
        db.SiteEnvVars.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task DeleteEnvVarAsync(Guid siteId, string envKey, CancellationToken ct)
    {
        var deleted = await db.SiteEnvVars.Where(v => v.SiteId == siteId && v.Key == envKey).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.SiteEnvVarNotFound, "Environment variable not found.");
    }

    /// <summary>Decrypted env vars for a site — consumed as Docker build args (<see cref="SiteBuildWorker"/>) and as container runtime env (<see cref="ActivateAsync"/>). A var that fails to decrypt (corrupted, or encrypted under a since-rotated key) is skipped rather than surfaced, same tolerance <c>FunctionExecutionService.BuildEnvAsync</c> uses.</summary>
    public async Task<Dictionary<string, string>> DecryptedEnvVarsAsync(Guid siteId, CancellationToken ct)
    {
        var stored = await db.SiteEnvVars.Where(v => v.SiteId == siteId).ToListAsync(ct);
        var env = new Dictionary<string, string>();
        foreach (var v in stored)
            if (key.Decrypt(v.ProtectedValue) is { } plain)
                env[v.Key] = plain;
        return env;
    }

    // ---- deployments ----------------------------------------------------------------------------

    public Task<List<SiteDeployment>> ListDeploymentsAsync(Guid siteId, CancellationToken ct) =>
        db.SiteDeployments.Where(d => d.SiteId == siteId).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);

    public async Task<SiteDeployment> GetDeploymentAsync(Guid siteId, Guid deploymentId, CancellationToken ct) =>
        await db.SiteDeployments.FirstOrDefaultAsync(d => d.Id == deploymentId && d.SiteId == siteId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.SiteDeploymentNotFound, "Deployment not found.");

    /// <summary>Stores the uploaded tar and queues a build — <see cref="SiteBuildWorker"/> does the rest.</summary>
    public async Task<SiteDeployment> CreateDeploymentAsync(Site site, byte[] tar, CancellationToken ct)
    {
        if (tar.Length == 0 || tar.Length > options.MaxSourceBytes)
            throw new PraxyException(400, ErrorTypes.SiteInvalidSource,
                $"Upload must be a non-empty tar under {options.MaxSourceBytes / (1024 * 1024)}MB.");

        var deployment = new SiteDeployment
        {
            Id = Ids.NewUuid(),
            SiteId = site.Id,
            ProjectId = site.ProjectId,
            SourceSizeBytes = tar.Length,
        };
        db.SiteDeployments.Add(deployment);
        db.SiteDeploymentSources.Add(new SiteDeploymentSource { DeploymentId = deployment.Id, Tar = tar });
        await db.SaveChangesAsync(ct);
        buildSignal.Notify();
        return deployment;
    }

    /// <summary>
    /// Starts <paramref name="deployment"/>'s container (or promotes one already warm as a preview —
    /// see below), fully through the same readiness probe as a cold start, points the site at it, and
    /// only then stops whatever was running before — a graceful blue-green swap, closing Phase 1's
    /// "known gap" (a brief stop-old/start-new downtime window on every redeploy). Called both by
    /// <see cref="SiteBuildWorker"/> (auto-activate the freshest successful build) and the console's
    /// explicit rollback endpoint (re-activate an older one).
    /// </summary>
    public async Task<SiteDeployment> ActivateAsync(Site site, SiteDeployment deployment, CancellationToken ct)
    {
        if (deployment.Status != "ready" || deployment.ImageTag is null)
            throw new PraxyException(400, ErrorTypes.SiteInvalidDeploymentState,
                $"Only a 'ready' deployment can be activated (this one is '{deployment.Status}').");

        // If this exact deployment was already running as an on-demand preview container (Phase 2),
        // promote it directly instead of starting a second one for the same image — an optimization,
        // not required for correctness (the cold-start branch below is just as correct).
        if (!registry.TryGet(deployment.Id, out var running))
        {
            var envVars = await DecryptedEnvVarsAsync(site.Id, ct);
            running = await docker.StartContainerAsync(deployment.ImageTag, envVars, deployment.Id.ToString(), ct);
        }

        var previousDeploymentId = site.ActiveDeploymentId;

        // Register the new deployment's container BEFORE flipping the DB pointer. The proxy
        // middleware resolves "the site's current container" as site.ActiveDeploymentId, then a
        // registry lookup by that id — if the DB write committed first, a request landing in the gap
        // would read the new deployment id but find no registry entry yet (a real, if brief, outage
        // this ordering exists to close). The old, site-keyed registry never had this problem because
        // Set() was a same-key replace; keying by deployment id (Phase 2) reintroduces it, so the
        // ordering has to close it explicitly instead. The proxy never observes a moment with no
        // entry to serve: readers between here and the DB commit still see the old deployment id and
        // find its still-running container untouched below.
        registry.Set(deployment.Id, running);

        site.ActiveDeploymentId = deployment.Id;
        site.UpdatedAt = DateTimeOffset.UtcNow;
        deployment.ContainerId = running.ContainerId;
        deployment.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (previousDeploymentId is { } prevId && prevId != deployment.Id)
        {
            await db.SiteDeployments.Where(d => d.Id == prevId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ContainerId, (string?)null), ct);
            if (registry.TryRemove(prevId, out var previousContainer))
                await docker.StopAndRemoveAsync(previousContainer.ContainerId, CancellationToken.None);
        }

        return deployment;
    }

    // ---- custom domains (Sites Phase 3) --------------------------------------------------------

    // A hostname with at least two labels (so a bare "localhost"-style single label can't be
    // registered), each 1-63 chars, letters/digits/hyphen, no leading/trailing hyphen per RFC 1035 —
    // '*' is already excluded by the character class, so a wildcard custom domain is rejected by
    // construction, not a separate check (Phase 3's own non-goal).
    [GeneratedRegex(@"^(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$")]
    private static partial Regex HostnamePattern();

    public Task<List<SiteDomain>> ListDomainsAsync(Guid siteId, CancellationToken ct) =>
        db.SiteDomains.Where(d => d.SiteId == siteId).OrderBy(d => d.CreatedAt).ToListAsync(ct);

    /// <summary>
    /// Registers a custom domain as <c>pending</c> — nothing here talks to Caddy or DNS; the row just
    /// makes the hostname a legitimate target for <see cref="SiteHostPattern"/>'s sibling lookup
    /// (<see cref="SiteCustomDomainLookup"/>) going forward. It flips to <c>verified</c> on its own,
    /// the first time a real request actually reaches <see cref="SiteProxyMiddleware"/> through it.
    /// </summary>
    public async Task<SiteDomain> AddDomainAsync(Site site, string hostname, CancellationToken ct)
    {
        var normalized = SiteCustomDomainLookup.Normalize(hostname);
        var reservedSuffix = "." + options.Domain;
        var invalid = normalized.Length > 253 || !HostnamePattern().IsMatch(normalized)
            || normalized == options.Domain || normalized.EndsWith(reservedSuffix, StringComparison.Ordinal);
        if (invalid)
            throw PraxyException.ArgumentInvalid("Invalid custom domain.", new Dictionary<string, string[]>
            {
                ["hostname"] = [$"Must be a valid DNS hostname (e.g. app.example.com) and not {options.Domain} or a subdomain of it."],
            });

        var domain = new SiteDomain
        {
            Id = Ids.NewUuid(),
            SiteId = site.Id,
            ProjectId = site.ProjectId,
            Hostname = normalized,
        };
        db.SiteDomains.Add(domain);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.SiteDomainAlreadyExists,
                $"'{normalized}' is already claimed by another site.");
        }
        return domain;
    }

    public async Task DeleteDomainAsync(Guid siteId, Guid domainId, CancellationToken ct)
    {
        var deleted = await db.SiteDomains.Where(d => d.Id == domainId && d.SiteId == siteId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.SiteDomainNotFound, "Custom domain not found.");
    }
}

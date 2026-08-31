using System.Text.RegularExpressions;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Vcs;

namespace Praxy.Functions;

/// <summary>
/// Console-facing CRUD for functions, their env vars, deployments and executions — metadata only,
/// same separation <c>WebhookSubscriptionsService</c> draws from its delivery pipeline. Building
/// (<see cref="FunctionBuildWorker"/>) and invoking (<see cref="FunctionExecutionService"/>) never
/// route through here.
/// </summary>
public sealed partial class FunctionsService(
    PraxyDb db, InstanceKey key, WarmPool pool, FunctionBuildSignal buildSignal, FunctionsOptions options,
    GitHubAppService github)
{
    /// <summary>Cap on a function's execute list — a bound like every other limit, not a silent free-for-all.</summary>
    public const int MaxExecuteRoles = 100;

    // ---- functions --------------------------------------------------------------------------

    public Task<List<FunctionDef>> ListAsync(string projectId, CancellationToken ct) =>
        db.Functions.Where(f => f.ProjectId == projectId).OrderByDescending(f => f.CreatedAt).ToListAsync(ct);

    public async Task<FunctionDef> GetAsync(string projectId, Guid id, CancellationToken ct) =>
        await db.Functions.FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.FunctionNotFound, "Function not found.");

    /// <summary>Whether the active deployment currently has a warm container — surfaced so a cold pool is visible, never a silent latency surprise (CLAUDE.md).</summary>
    public bool IsWarm(FunctionDef fn) => fn.ActiveDeploymentId is { } id && pool.IsWarm(id);

    public async Task<FunctionDef> CreateAsync(
        string projectId, string key_, string name, string runtime, string entrypoint, int timeoutSeconds,
        string[] events, string[] execute, string? schedule, CancellationToken ct)
    {
        var fields = Validate(key_, name, runtime, entrypoint, timeoutSeconds, events, execute, schedule, out var nextRun);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid function payload.", fields);

        var fn = new FunctionDef
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Key = key_.Trim(),
            Name = name.Trim(),
            Runtime = runtime,
            Entrypoint = entrypoint.Trim(),
            TimeoutSeconds = timeoutSeconds,
            Events = [.. events.Select(e => e.Trim())],
            Execute = NormalizeExecute(execute),
            Schedule = schedule,
            NextScheduledRunAt = nextRun,
        };
        db.Functions.Add(fn);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.FunctionAlreadyExists,
                $"A function with key '{key_}' already exists in this project.");
        }
        return fn;
    }

    /// <summary>
    /// <paramref name="schedule"/> follows the same write-only-secret convention
    /// <c>UpdateAuthSettingsRequest.GoogleClientSecret</c> uses: null means unchanged, empty string
    /// clears the schedule, anything else is a new cron expression.
    /// </summary>
    public async Task<FunctionDef> UpdateAsync(
        FunctionDef fn, string? name, string? entrypoint, int? timeoutSeconds, string[]? events, string[]? execute,
        string? schedule, bool? enabled, CancellationToken ct)
    {
        var effectiveSchedule = schedule switch { null => fn.Schedule, "" => null, var s => s };
        var fields = Validate(
            fn.Key, name ?? fn.Name, fn.Runtime, entrypoint ?? fn.Entrypoint, timeoutSeconds ?? fn.TimeoutSeconds,
            events ?? fn.Events, execute ?? fn.Execute, effectiveSchedule, out var nextRun);
        if (fields.Count > 0)
            throw PraxyException.ArgumentInvalid("Invalid function payload.", fields);

        if (name is not null)
            fn.Name = name.Trim();
        if (entrypoint is not null)
            fn.Entrypoint = entrypoint.Trim();
        if (timeoutSeconds is { } t)
            fn.TimeoutSeconds = t;
        if (events is not null)
            fn.Events = [.. events.Select(e => e.Trim())];
        if (execute is not null)
            fn.Execute = NormalizeExecute(execute);
        if (schedule is not null)
        {
            fn.Schedule = effectiveSchedule;
            fn.NextScheduledRunAt = nextRun;
        }
        if (enabled is { } e2)
            fn.Enabled = e2;
        fn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return fn;
    }

    /// <summary>
    /// Creates a function from a bundled <see cref="FunctionTemplates"/> starter and deploys its
    /// tar as the first deployment — reuses <see cref="CreateAsync"/> and
    /// <see cref="CreateDeploymentAsync"/> unchanged rather than duplicating their validation, the
    /// same discipline <c>SitesService</c>'s starter-template path follows. The function starts with
    /// no granted execute roles, same as a manually created one — deny by default.
    /// </summary>
    public async Task<(FunctionDef Function, FunctionDeployment Deployment)> CreateFromTemplateAsync(
        string projectId, string templateKey, string key_, string name, CancellationToken ct)
    {
        var template = FunctionTemplates.Find(templateKey);
        var fn = await CreateAsync(
            projectId, key_, name, template.Runtime, template.Entrypoint, 15, [], [], template.DefaultSchedule, ct);
        var tar = await FunctionTemplates.BuildTarAsync(templateKey, ct);
        var deployment = await CreateDeploymentAsync(fn, tar, ct);
        return (fn, deployment);
    }

    public async Task DeleteAsync(FunctionDef fn, CancellationToken ct)
    {
        if (fn.ActiveDeploymentId is { } active)
            await pool.EvictAsync(active, CancellationToken.None);
        db.Functions.Remove(fn);
        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, string[]> Validate(
        string key_, string name, string runtime, string entrypoint, int timeoutSeconds, string[] events,
        string[] execute, string? schedule, out DateTimeOffset? nextScheduledRunAt)
    {
        nextScheduledRunAt = null;
        var fields = new Dictionary<string, string[]>();

        if (!Ids.IsValidCustomId(key_))
            fields["key"] = ["1-36 chars, lowercase alphanumeric or hyphen, must start alphanumeric."];
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128)
            fields["name"] = ["Must be between 1 and 128 characters."];
        if (!FunctionRuntimes.All.Contains(runtime))
            fields["runtime"] = [$"Must be one of: {string.Join(", ", FunctionRuntimes.All)}."];
        else if (!FunctionRuntimes.IsValidEntrypoint(runtime, entrypoint))
            fields["entrypoint"] = ["Must be a relative path with no '..' segments, matching the runtime's file extension."];
        if (timeoutSeconds is < 1 or > 900)
            fields["timeoutSeconds"] = ["Must be between 1 and 900 seconds."];
        if (events.Any(ev => !IsValidEventPattern(ev)))
            fields["events"] = ["Each pattern must be dot-separated segments of letters, digits, underscore or '*' (e.g. 'databases.*.tables.*.rows.*.create')."];

        if (execute.Length > MaxExecuteRoles)
            fields["execute"] = [$"At most {MaxExecuteRoles} roles."];
        else if (execute.Any(r => r is null || !Roles.IsValid(r.Trim())))
            fields["execute"] = ["Each entry must be a role: any, guests, users, users/verified, user:<id>[/verified], team:<id>[/<role>], member:<id> or label:<name>."];

        if (!string.IsNullOrWhiteSpace(schedule))
        {
            try
            {
                nextScheduledRunAt = CronExpression.Parse(schedule).GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
            }
            catch (CronFormatException)
            {
                fields["schedule"] = ["Must be a standard 5-field cron expression (minute hour day-of-month month day-of-week)."];
            }
        }

        return fields;
    }

    /// <summary>
    /// Whether <paramref name="callerRoles"/> may invoke <paramref name="fn"/> over the data plane.
    /// An empty <c>Execute</c> list denies everyone — the deny-by-default state every function is
    /// created in.
    /// </summary>
    public static bool CanExecute(FunctionDef fn, IEnumerable<string> callerRoles) =>
        fn.Execute.Intersect(callerRoles).Any();

    private static string[] NormalizeExecute(string[] execute) =>
        [.. execute.Select(r => r.Trim()).Distinct()];

    private static bool IsValidEventPattern(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) &&
        pattern.Split('.').All(part => part.Length > 0 &&
            part.All(c => c == '*' || char.IsAsciiLetterOrDigit(c) || c == '_'));

    // ---- env vars -----------------------------------------------------------------------------

    /// <summary>Keys only — values are write-only from the console's perspective, same as a webhook secret or an OAuth client secret.</summary>
    public Task<List<FunctionEnvVar>> ListEnvVarsAsync(Guid functionId, CancellationToken ct) =>
        db.FunctionEnvVars.Where(v => v.FunctionId == functionId).OrderBy(v => v.Key).ToListAsync(ct);

    public async Task<FunctionEnvVar> SetEnvVarAsync(Guid functionId, string envKey, string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envKey) || envKey.Length > 256 || !envKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw PraxyException.ArgumentInvalid("Invalid environment variable key.",
                new Dictionary<string, string[]> { ["key"] = ["1-256 chars: letters, digits, underscore."] });

        var existing = await db.FunctionEnvVars.FirstOrDefaultAsync(v => v.FunctionId == functionId && v.Key == envKey, ct);
        if (existing is not null)
        {
            existing.ProtectedValue = key.Encrypt(value);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var created = new FunctionEnvVar
        {
            Id = Ids.NewUuid(),
            FunctionId = functionId,
            Key = envKey,
            ProtectedValue = key.Encrypt(value),
        };
        db.FunctionEnvVars.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task DeleteEnvVarAsync(Guid functionId, string envKey, CancellationToken ct)
    {
        var deleted = await db.FunctionEnvVars
            .Where(v => v.FunctionId == functionId && v.Key == envKey)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw PraxyException.NotFound(ErrorTypes.FunctionEnvVarNotFound, "Environment variable not found.");
    }

    // ---- deployments ----------------------------------------------------------------------------

    public Task<List<FunctionDeployment>> ListDeploymentsAsync(Guid functionId, CancellationToken ct) =>
        db.FunctionDeployments.Where(d => d.FunctionId == functionId).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);

    public async Task<FunctionDeployment> GetDeploymentAsync(Guid functionId, Guid deploymentId, CancellationToken ct) =>
        await db.FunctionDeployments.FirstOrDefaultAsync(d => d.Id == deploymentId && d.FunctionId == functionId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.FunctionDeploymentNotFound, "Deployment not found.");

    /// <summary>Stores the uploaded tar and queues a build — <see cref="FunctionBuildWorker"/> does the rest.</summary>
    public async Task<FunctionDeployment> CreateDeploymentAsync(FunctionDef fn, byte[] tar, CancellationToken ct)
    {
        if (tar.Length == 0 || tar.Length > options.MaxSourceBytes)
            throw new PraxyException(400, ErrorTypes.FunctionInvalidSource,
                $"Upload must be a non-empty tar under {options.MaxSourceBytes / (1024 * 1024)}MB.");

        var deployment = new FunctionDeployment
        {
            Id = Ids.NewUuid(),
            FunctionId = fn.Id,
            ProjectId = fn.ProjectId,
            SourceSizeBytes = tar.Length,
        };
        db.FunctionDeployments.Add(deployment);
        db.FunctionDeploymentSources.Add(new FunctionDeploymentSource { DeploymentId = deployment.Id, Tar = tar });
        await db.SaveChangesAsync(ct);
        buildSignal.Notify();
        return deployment;
    }

    /// <summary>Explicit rollback/promote — the build worker already auto-activates the newest successful build, so this exists for reverting to an older one.</summary>
    public async Task<FunctionDeployment> ActivateAsync(FunctionDef fn, FunctionDeployment deployment, CancellationToken ct)
    {
        if (deployment.Status != "ready")
            throw new PraxyException(400, ErrorTypes.FunctionInvalidDeploymentState,
                $"Only a 'ready' deployment can be activated (this one is '{deployment.Status}').");

        var previous = fn.ActiveDeploymentId;
        fn.ActiveDeploymentId = deployment.Id;
        fn.UpdatedAt = DateTimeOffset.UtcNow;
        deployment.ActivatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (previous is { } prev && prev != deployment.Id)
            await pool.EvictAsync(prev, CancellationToken.None);
        return deployment;
    }

    // ---- executions -------------------------------------------------------------------------------

    public async Task<FunctionExecution> CreateExecutionAsync(
        FunctionDef fn, string trigger, bool async, string method, string path, string? body, string? triggeredBy,
        CancellationToken ct)
    {
        var execution = new FunctionExecution
        {
            Id = Ids.NewUuid(),
            FunctionId = fn.Id,
            ProjectId = fn.ProjectId,
            Trigger = trigger,
            Async = async,
            Method = method,
            Path = path,
            RequestBody = body,
            TriggeredBy = triggeredBy,
        };
        db.FunctionExecutions.Add(execution);
        await db.SaveChangesAsync(ct);
        return execution;
    }

    // AsNoTracking on both: a sync invocation creates (and thereby tracks) the execution row, then
    // FunctionExecutionService finalizes it via ExecuteUpdateAsync — a bulk statement that updates
    // the database directly without touching the change tracker. Without AsNoTracking, re-reading
    // the same row in the same DbContext scope would resolve to the *stale* tracked instance (still
    // "waiting") instead of the fresh database values EF's identity map would otherwise mask.
    public Task<(int Total, List<FunctionExecution> Page)> ListExecutionsAsync(
        Guid functionId, int limit, int offset, CancellationToken ct) =>
        PageAsync(db.FunctionExecutions.AsNoTracking().Where(e => e.FunctionId == functionId).OrderByDescending(e => e.CreatedAt), limit, offset, ct);

    public async Task<FunctionExecution> GetExecutionAsync(Guid functionId, Guid executionId, CancellationToken ct) =>
        await db.FunctionExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId && e.FunctionId == functionId, ct)
        ?? throw PraxyException.NotFound(ErrorTypes.FunctionExecutionNotFound, "Execution not found.");

    private static async Task<(int, List<FunctionExecution>)> PageAsync(
        IQueryable<FunctionExecution> query, int limit, int offset, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var page = await query.Skip(offset).Take(limit).ToListAsync(ct);
        return (total, page);
    }

    // ---- git repository (Functions git integration) --------------------------------------------

    // GitHub's own username/repo-name rules, permissively — the real gate is EnsureRepositoryAccessibleAsync
    // actually asking GitHub, this just keeps obviously-malformed input from reaching that call.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    /// <summary>Current connection state — both null until a repository is connected.</summary>
    public (string? RepositoryFullName, string? ProductionBranch) GitConnection(FunctionDef fn) =>
        (fn.RepositoryFullName, fn.ProductionBranch);

    /// <summary>
    /// Connects <paramref name="fn"/> to a GitHub repository: validates the <c>owner/repo</c> shape,
    /// confirms the instance's GitHub App installation actually covers it, and confirms
    /// <paramref name="productionBranch"/> is a real branch of that repository — mirrors
    /// <c>SitesService.ConnectRepositoryAsync</c> exactly.
    /// </summary>
    public async Task<FunctionDef> ConnectRepositoryAsync(FunctionDef fn, string repositoryFullName, string productionBranch, CancellationToken ct)
    {
        if (!RepositoryPattern().IsMatch(repositoryFullName) || string.IsNullOrWhiteSpace(productionBranch) || productionBranch.Length > 256)
            throw new PraxyException(400, ErrorTypes.FunctionGitRepositoryInvalid,
                "Invalid repository or branch.");

        await github.EnsureRepositoryAccessibleAsync(repositoryFullName, ct);
        var branches = await github.ListBranchesForRepositoryAsync(repositoryFullName, ct);
        if (!branches.Contains(productionBranch))
            throw new PraxyException(400, ErrorTypes.FunctionGitRepositoryInvalid,
                $"'{productionBranch}' is not a branch of '{repositoryFullName}'.");

        fn.RepositoryFullName = repositoryFullName;
        fn.ProductionBranch = productionBranch;
        fn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return fn;
    }

    public async Task<FunctionDef> DisconnectRepositoryAsync(FunctionDef fn, CancellationToken ct)
    {
        fn.RepositoryFullName = null;
        fn.ProductionBranch = null;
        fn.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return fn;
    }

    /// <summary>Queues a git-sourced build — no <see cref="FunctionDeploymentSource"/> row (there are no uploaded bytes); <see cref="FunctionBuildWorker"/> clones the commit fresh at build time.</summary>
    public async Task<FunctionDeployment> CreateGitDeploymentAsync(
        FunctionDef fn, string commitSha, string commitMessage, string branch, CancellationToken ct)
    {
        var deployment = new FunctionDeployment
        {
            Id = Ids.NewUuid(),
            FunctionId = fn.Id,
            ProjectId = fn.ProjectId,
            Source = "git",
            CommitSha = commitSha,
            CommitMessage = commitMessage,
            Branch = branch,
        };
        db.FunctionDeployments.Add(deployment);
        await db.SaveChangesAsync(ct);
        buildSignal.Notify();
        return deployment;
    }

    /// <summary>
    /// The webhook's dispatch target for Functions — matches a parsed push event against connected
    /// functions and queues a deployment for each. A direct parallel query to
    /// <c>SitesService.HandleGitPushAsync</c>, not a shared abstraction — the same repository can be
    /// connected to a site and a function simultaneously, and a single push must trigger both
    /// independently. A push for a repository no function references is a no-op, not an error.
    /// </summary>
    public async Task<int> HandleGitPushAsync(GitHubPushEvent evt, CancellationToken ct)
    {
        var matches = await db.Functions.Where(f => f.RepositoryFullName == evt.RepositoryFullName).ToListAsync(ct);
        foreach (var fn in matches)
            await CreateGitDeploymentAsync(fn, evt.CommitSha, evt.CommitMessage, evt.Branch, ct);
        return matches.Count;
    }
}

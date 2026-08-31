using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Functions;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Vcs;

namespace Praxy.Api.Endpoints;

public sealed record CreateFunctionRequest(
    string Key, string Name, string Runtime, string Entrypoint, int? TimeoutSeconds, string[]? Events,
    string[]? Execute, string? Schedule);

public sealed record UpdateFunctionRequest(
    string? Name, string? Entrypoint, int? TimeoutSeconds, string[]? Events, string[]? Execute, string? Schedule,
    bool? Enabled, string[]? PlatformScopes);

public sealed record SetEnvVarRequest(string Value);

public sealed record InvokeFunctionRequest(string? Method, string? Path, string? Body);

public sealed record ConnectFunctionGitRequest(string RepositoryFullName, string ProductionBranch);

public sealed record FunctionRuntimeResponse(string Id, string BaseImage);

public sealed record FunctionResponse(
    string Id, string Key, string Name, string Runtime, string Entrypoint, int TimeoutSeconds, bool Enabled,
    string[] Events, string[] Execute, string? Schedule, DateTimeOffset? NextScheduledRunAt,
    string? ActiveDeploymentId, bool IsWarm, string? RepositoryFullName, string? ProductionBranch,
    string[] PlatformScopes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static FunctionResponse From(FunctionDef f, bool isWarm) => new(
        Ids.Wire(f.Id), f.Key, f.Name, f.Runtime, f.Entrypoint, f.TimeoutSeconds, f.Enabled, f.Events, f.Execute,
        f.Schedule, f.NextScheduledRunAt, f.ActiveDeploymentId is { } d ? Ids.Wire(d) : null, isWarm,
        f.RepositoryFullName, f.ProductionBranch, f.PlatformScopes, f.CreatedAt, f.UpdatedAt);
}

public sealed record FunctionEnvVarResponse(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static FunctionEnvVarResponse From(FunctionEnvVar v) => new(v.Key, v.CreatedAt, v.UpdatedAt);
}

public sealed record FunctionDeploymentResponse(
    string Id, string Status, long SourceSizeBytes, string Source, string? CommitSha, string? CommitMessage,
    string? Branch, string BuildLog, string? Error, string? ImageTag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ActivatedAt)
{
    public static FunctionDeploymentResponse From(FunctionDeployment d) => new(
        Ids.Wire(d.Id), d.Status, d.SourceSizeBytes, d.Source, d.CommitSha, d.CommitMessage, d.Branch,
        d.BuildLog, d.Error, d.ImageTag, d.CreatedAt, d.UpdatedAt, d.ActivatedAt);
}

public sealed record FunctionExecutionResponse(
    string Id, string Trigger, bool Async, string Status, string Method, string Path, int? StatusCode,
    string? ResponseBody, string Logs, string? Errors, int? DurationMs, bool ColdStart, string? TriggeredBy,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static FunctionExecutionResponse From(FunctionExecution e) => new(
        Ids.Wire(e.Id), e.Trigger, e.Async, e.Status, e.Method, e.Path, e.StatusCode, e.ResponseBody, e.Logs,
        e.Errors, e.DurationMs, e.ColdStart, e.TriggeredBy, e.CreatedAt, e.CompletedAt);
}

public sealed record FunctionGitBranchesResponse(IReadOnlyList<string> Branches);

public sealed record FunctionListResponse(int Total, IReadOnlyList<FunctionResponse> Functions);

public sealed record FunctionRuntimeListResponse(IReadOnlyList<FunctionRuntimeResponse> Runtimes);

public sealed record FunctionEnvVarListResponse(int Total, IReadOnlyList<FunctionEnvVarResponse> Vars);

public sealed record FunctionDeploymentListResponse(
    int Total, IReadOnlyList<FunctionDeploymentResponse> Deployments);

public sealed record FunctionExecutionListResponse(
    int Total, IReadOnlyList<FunctionExecutionResponse> Executions);

/// <summary>
/// Functions (Phase 7): console admin surface for deploying/configuring/inspecting functions, plus
/// the data-plane invocation endpoint app users and API keys call. Same operator-filter chain and
/// audit-log convention as <see cref="WebhookEndpoints"/> for the console half; the data-plane half
/// mirrors <see cref="RowEndpoints"/>'s <c>AppPrincipalFilter</c> shape so a scoped user JWT can be
/// minted for whichever caller triggered the invocation.
///
/// Data-plane invocation is authorized by the function's <c>execute</c> role list, which is empty on
/// a new function — deny by default, the same posture a new table has. Server-side trigger paths
/// (console invoke, event dispatch, cron) are operator-configured and carry no external caller, so
/// they are not gated; see <see cref="RequireExecutePermissionAsync"/>.
///
/// A data-plane caller may also read back one execution it triggered
/// (<see cref="GetDataPlaneExecution"/>) — the async half of invocation without this would return a
/// receipt for a result nobody could ever collect. Scoped to the caller's own execution, not the
/// function's <c>execute</c> role: two different callers permitted to invoke the same function must
/// not be able to read each other's request/response bodies and logs.
/// </summary>
public static class FunctionEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/functions")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("", ListFunctions)
            .Produces<FunctionListResponse>();
        admin.MapGet("/runtimes", ListRuntimes)
            .Produces<FunctionRuntimeListResponse>();
        admin.MapPost("", CreateFunction)
            .Produces<FunctionResponse>(StatusCodes.Status201Created);
        admin.MapGet("/{functionId}", GetFunction)
            .Produces<FunctionResponse>();
        admin.MapPatch("/{functionId}", UpdateFunction)
            .Produces<FunctionResponse>();
        admin.MapDelete("/{functionId}", DeleteFunction)
            .Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/{functionId}/env", ListEnvVars)
            .Produces<FunctionEnvVarListResponse>();
        admin.MapPut("/{functionId}/env/{envKey}", SetEnvVar)
            .Produces<FunctionEnvVarResponse>();
        admin.MapDelete("/{functionId}/env/{envKey}", DeleteEnvVar)
            .Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/{functionId}/git/branches", ListGitBranches).Produces<FunctionGitBranchesResponse>();
        admin.MapPost("/{functionId}/git", ConnectGit).Produces<FunctionResponse>();
        admin.MapDelete("/{functionId}/git", DisconnectGit).Produces<FunctionResponse>();

        admin.MapGet("/{functionId}/deployments", ListDeployments)
            .Produces<FunctionDeploymentListResponse>();
        admin.MapPost("/{functionId}/deployments", CreateDeployment)
            .Produces<FunctionDeploymentResponse>(StatusCodes.Status201Created);
        admin.MapGet("/{functionId}/deployments/{deploymentId}", GetDeployment)
            .Produces<FunctionDeploymentResponse>();
        admin.MapPost("/{functionId}/deployments/{deploymentId}/activate", ActivateDeployment)
            .Produces<FunctionDeploymentResponse>();

        admin.MapGet("/{functionId}/executions", ListExecutions)
            .Produces<FunctionExecutionListResponse>();
        admin.MapGet("/{functionId}/executions/{executionId}", GetExecution)
            .Produces<FunctionExecutionResponse>();
        admin.MapPost("/{functionId}/executions", ConsoleInvoke)
            .Produces<FunctionExecutionResponse>();

        // Tighter than the rest of the data plane: every permitted request here can start a
        // container, so this is the one bucket where the limit is about capacity, not just abuse.
        var dataPlane = api.MapGroup("/v1/functions")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>()
            .RequireRateLimiting("functions");

        dataPlane.MapPost("/{functionId}/executions", Invoke)
            .Produces<FunctionExecutionResponse>()
            .Produces<FunctionExecutionResponse>(StatusCodes.Status202Accepted);
        // The read half of async invocation: a 202 with nowhere to poll it afterward isn't a
        // feature. Scoped to the caller's own execution — see GetDataPlaneExecution.
        dataPlane.MapGet("/{functionId}/executions/{executionId}", GetDataPlaneExecution)
            .Produces<FunctionExecutionResponse>();
        // execution.read only — see ListDataPlaneExecutions.
        dataPlane.MapGet("/{functionId}/executions", ListDataPlaneExecutions)
            .Produces<FunctionExecutionListResponse>();

        // The server-side management surface: a functions.read/functions.write key manages
        // functions exactly as a console operator does. Same group (rate limit included — a
        // deployment upload builds a container image, just as costly as an invocation) and the
        // same underlying FunctionsService calls the console handlers above use; a key never had
        // any way to do this before, which meant a CI/CD pipeline deploying a new build always
        // needed a human operator's browser session.
        dataPlane.MapGet("", ServerListFunctions)
            .Produces<FunctionListResponse>();
        dataPlane.MapGet("/runtimes", ServerListRuntimes)
            .Produces<FunctionRuntimeListResponse>();
        dataPlane.MapPost("", ServerCreateFunction)
            .Produces<FunctionResponse>(StatusCodes.Status201Created);
        dataPlane.MapGet("/{functionId}", ServerGetFunction)
            .Produces<FunctionResponse>();
        dataPlane.MapPatch("/{functionId}", ServerUpdateFunction)
            .Produces<FunctionResponse>();
        dataPlane.MapDelete("/{functionId}", ServerDeleteFunction)
            .Produces(StatusCodes.Status204NoContent);

        dataPlane.MapGet("/{functionId}/env", ServerListEnvVars)
            .Produces<FunctionEnvVarListResponse>();
        dataPlane.MapPut("/{functionId}/env/{envKey}", ServerSetEnvVar)
            .Produces<FunctionEnvVarResponse>();
        dataPlane.MapDelete("/{functionId}/env/{envKey}", ServerDeleteEnvVar)
            .Produces(StatusCodes.Status204NoContent);

        dataPlane.MapGet("/{functionId}/deployments", ServerListDeployments)
            .Produces<FunctionDeploymentListResponse>();
        dataPlane.MapPost("/{functionId}/deployments", ServerCreateDeployment)
            .Produces<FunctionDeploymentResponse>(StatusCodes.Status201Created);
        dataPlane.MapGet("/{functionId}/deployments/{deploymentId}", ServerGetDeployment)
            .Produces<FunctionDeploymentResponse>();
        dataPlane.MapPost("/{functionId}/deployments/{deploymentId}/activate", ServerActivateDeployment)
            .Produces<FunctionDeploymentResponse>();
    }

    // ---- functions ------------------------------------------------------------------------------

    private static async Task<IResult> ListFunctions(HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await functions.ListAsync(project.Id, ct);
        return Results.Ok(new FunctionListResponse(
            list.Count, [.. list.Select(f => FunctionResponse.From(f, functions.IsWarm(f)))]));
    }

    /// <summary>
    /// Base images are an operator config knob (<c>Praxy:Functions:DartBaseImage</c>/<c>NodeBaseImage</c>,
    /// self-host.md documents both), not a hardcoded constant — the console's runtime picker calls this
    /// instead of assuming the upstream defaults, so a self-hoster who pinned a different tag sees their
    /// actual pin, not a stale guess.
    /// </summary>
    private static IResult ListRuntimes(FunctionsOptions options) =>
        Results.Ok(new FunctionRuntimeListResponse(
            [.. FunctionRuntimes.All.Select(r => new FunctionRuntimeResponse(
                r, r == FunctionRuntimes.Dart ? options.DartBaseImage : options.NodeBaseImage))]));

    private static async Task<IResult> CreateFunction(
        CreateFunctionRequest req, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await functions.CreateAsync(
            project.Id, req.Key, req.Name, req.Runtime, req.Entrypoint, req.TimeoutSeconds ?? 15, req.Events ?? [],
            req.Execute ?? [], req.Schedule, ct);
        await AuditAsync(db, http, project.Id, "functions.create", $"function/{Ids.Wire(fn.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/functions/{Ids.Wire(fn.Id)}", FunctionResponse.From(fn, false));
    }

    private static async Task<IResult> GetFunction(string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        return Results.Ok(FunctionResponse.From(fn, functions.IsWarm(fn)));
    }

    private static async Task<IResult> UpdateFunction(
        string functionId, UpdateFunctionRequest req, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var updated = await functions.UpdateAsync(
            fn, req.Name, req.Entrypoint, req.TimeoutSeconds, req.Events, req.Execute, req.Schedule, req.Enabled,
            req.PlatformScopes, ct);
        // A permission change gets its own action string. The audit log records what was touched but
        // not what it became, so folding "who may run this" into the same `functions.update` as a
        // timeout tweak would make the one security-relevant edit here invisible to anyone reading
        // the log later. Platform scopes take priority over execute when a single request touches both.
        await AuditAsync(db, http, project.Id, PermissionAuditAction(req.Execute, req.PlatformScopes),
            $"function/{functionId}", ct);
        return Results.Ok(FunctionResponse.From(updated, functions.IsWarm(updated)));
    }

    private static string PermissionAuditAction(string[]? execute, string[]? platformScopes) =>
        platformScopes is not null ? "functions.platform_scopes.update"
        : execute is not null ? "functions.execute.update"
        : "functions.update";

    private static async Task<IResult> DeleteFunction(
        string functionId, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        await functions.DeleteAsync(fn, ct);
        await AuditAsync(db, http, project.Id, "functions.delete", $"function/{functionId}", ct);
        return Results.NoContent();
    }

    // ---- env vars -------------------------------------------------------------------------------

    private static async Task<IResult> ListEnvVars(string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var list = await functions.ListEnvVarsAsync(fn.Id, ct);
        return Results.Ok(new FunctionEnvVarListResponse(list.Count, [.. list.Select(FunctionEnvVarResponse.From)]));
    }

    private static async Task<IResult> SetEnvVar(
        string functionId, string envKey, SetEnvVarRequest req, HttpContext http, PraxyDb db,
        FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var v = await functions.SetEnvVarAsync(fn.Id, envKey, req.Value, ct);
        await AuditAsync(db, http, project.Id, "functions.env.set", $"function/{functionId}/env/{envKey}", ct);
        return Results.Ok(FunctionEnvVarResponse.From(v));
    }

    private static async Task<IResult> DeleteEnvVar(
        string functionId, string envKey, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        await functions.DeleteEnvVarAsync(fn.Id, envKey, ct);
        await AuditAsync(db, http, project.Id, "functions.env.delete", $"function/{functionId}/env/{envKey}", ct);
        return Results.NoContent();
    }

    // ---- git repository (Functions git integration) ---------------------------------------------

    private static async Task<IResult> ListGitBranches(
        string functionId, string repository, HttpContext http, FunctionsService functions, GitHubAppService github, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        await FindAsync(functions, project.Id, functionId, ct);
        var branches = await github.ListBranchesForRepositoryAsync(repository, ct);
        return Results.Ok(new FunctionGitBranchesResponse(branches));
    }

    private static async Task<IResult> ConnectGit(
        string functionId, ConnectFunctionGitRequest req, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var connected = await functions.ConnectRepositoryAsync(fn, req.RepositoryFullName, req.ProductionBranch, ct);
        await AuditAsync(db, http, project.Id, "functions.git.connect", $"function/{functionId}", ct);
        return Results.Ok(FunctionResponse.From(connected, functions.IsWarm(connected)));
    }

    private static async Task<IResult> DisconnectGit(
        string functionId, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var disconnected = await functions.DisconnectRepositoryAsync(fn, ct);
        await AuditAsync(db, http, project.Id, "functions.git.disconnect", $"function/{functionId}", ct);
        return Results.Ok(FunctionResponse.From(disconnected, functions.IsWarm(disconnected)));
    }

    // ---- deployments ----------------------------------------------------------------------------

    private static async Task<IResult> ListDeployments(string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var list = await functions.ListDeploymentsAsync(fn.Id, ct);
        return Results.Ok(new FunctionDeploymentListResponse(
            list.Count, [.. list.Select(FunctionDeploymentResponse.From)]));
    }

    private static async Task<IResult> CreateDeployment(
        string functionId, HttpContext http, PraxyDb db, FunctionsService functions, FunctionsOptions options, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var tar = await ReadCappedAsync(http.Request.Body, options.MaxSourceBytes, ct);
        var deployment = await functions.CreateDeploymentAsync(fn, tar, ct);
        await AuditAsync(db, http, project.Id, "functions.deployments.create", $"function/{functionId}/deployment/{Ids.Wire(deployment.Id)}", ct);
        return Results.Created(
            $"/v1/console/projects/{project.Id}/functions/{functionId}/deployments/{Ids.Wire(deployment.Id)}",
            FunctionDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> GetDeployment(
        string functionId, string deploymentId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var deployment = await FindDeploymentAsync(functions, fn.Id, deploymentId, ct);
        return Results.Ok(FunctionDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> ActivateDeployment(
        string functionId, string deploymentId, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var deployment = await FindDeploymentAsync(functions, fn.Id, deploymentId, ct);
        var activated = await functions.ActivateAsync(fn, deployment, ct);
        await AuditAsync(db, http, project.Id, "functions.deployments.activate", $"function/{functionId}/deployment/{deploymentId}", ct);
        return Results.Ok(FunctionDeploymentResponse.From(activated));
    }

    // ---- executions -----------------------------------------------------------------------------

    private static async Task<IResult> ListExecutions(
        string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var (limit, offset) = ListParams(http);
        var (total, page) = await functions.ListExecutionsAsync(fn.Id, limit, offset, ct);
        return Results.Ok(new FunctionExecutionListResponse(total, [.. page.Select(FunctionExecutionResponse.From)]));
    }

    private static async Task<IResult> GetExecution(
        string functionId, string executionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var execution = await FindExecutionAsync(functions, fn.Id, executionId, ct);
        return Results.Ok(FunctionExecutionResponse.From(execution));
    }

    /// <summary>The console's "Run" / test-invoke button — always as trigger "http", never scoped to an app user (operators aren't app users, so no JWT is minted for it).</summary>
    /// <summary>
    /// The operator's own invoke. Deliberately NOT gated on the function's <c>execute</c> roles: the
    /// caller is already an authenticated operator on this project, and this is the escape hatch that
    /// keeps a freshly created (deny-by-default) function testable before any role is granted.
    /// </summary>
    private static async Task<IResult> ConsoleInvoke(
        string functionId, InvokeFunctionRequest req, HttpContext http, PraxyDb db,
        FunctionsService functions, FunctionExecutionService runner, FunctionExecutionSignal signal, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        RequireInvokable(fn);

        var isAsync = bool.TryParse(http.Request.Query["async"], out var a) && a;
        var execution = await functions.CreateExecutionAsync(
            fn, "http", isAsync, req.Method ?? "GET", req.Path ?? "/", req.Body, "console", ct);
        await AuditAsync(db, http, project.Id, "functions.invoke", $"function/{functionId}/execution/{Ids.Wire(execution.Id)}", ct);

        if (isAsync)
        {
            signal.Notify();
            return Results.Accepted(
                $"/v1/console/projects/{project.Id}/functions/{functionId}/executions/{Ids.Wire(execution.Id)}",
                FunctionExecutionResponse.From(execution));
        }

        await runner.RunAsync(execution, ct);
        var completed = await functions.GetExecutionAsync(fn.Id, execution.Id, ct);
        return Results.Ok(FunctionExecutionResponse.From(completed));
    }

    /// <summary>Data-plane invocation: app-user sessions, JWTs and API keys. Sync unless <c>?async=true</c>.</summary>
    private static async Task<IResult> Invoke(
        string functionId, InvokeFunctionRequest req, HttpContext http,
        FunctionsService functions, FunctionExecutionService runner, FunctionExecutionSignal signal,
        IRoleResolver roleResolver, CancellationToken ct)
    {
        if (AppPrincipalFilter.Current(http) is RequestPrincipal.Key)
            AppPrincipalFilter.RequireScope(http, ApiKeyScopes.ExecutionWrite);

        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(functionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.FunctionNotFound, "Function not found.");
        var fn = await functions.GetAsync(project.Id, parsed, ct);
        // Authorize before reporting state: an unauthorized caller gets the same 401 whether or not
        // the function is disabled or undeployed, rather than being told which.
        await RequireExecutePermissionAsync(http, fn, roleResolver);
        RequireInvokable(fn);

        var isAsync = bool.TryParse(http.Request.Query["async"], out var a) && a;
        var execution = await functions.CreateExecutionAsync(
            fn, "http", isAsync, req.Method ?? "GET", req.Path ?? "/", req.Body, CallerIdentity(http) ?? "guest", ct);

        if (isAsync)
        {
            signal.Notify();
            return Results.Accepted(
                $"/v1/functions/{functionId}/executions/{Ids.Wire(execution.Id)}", FunctionExecutionResponse.From(execution));
        }

        await runner.RunAsync(execution, ct);
        var completed = await functions.GetExecutionAsync(fn.Id, execution.Id, ct);
        return Results.Ok(FunctionExecutionResponse.From(completed));
    }

    /// <summary>
    /// The data plane's own execution read — the other half of async invocation. Default scope is
    /// "your own execution only": a caller may read an execution whose stored
    /// <see cref="FunctionExecution.TriggeredBy"/> matches their own <see cref="CallerIdentity"/>,
    /// nothing wider. The looser alternative — anyone holding the function's <c>execute</c> role may
    /// read any execution of it — was rejected: two unrelated app users who both satisfy
    /// <c>execute("users")</c> on a public function would otherwise be able to read each other's
    /// request bodies, response bodies and logs. Own-execution-only never crosses that line.
    ///
    /// A key explicitly holding <see cref="ApiKeyScopes.ExecutionRead"/> (or bypass) gets the wider
    /// read Appwrite's equivalent scope implies — any execution of the function, not just its own —
    /// because that grant is an operator's own deliberate choice on a key they issued, not something
    /// an app-facing <c>execute</c> role can trigger on someone else's behalf.
    ///
    /// A caller who fails the check gets the same <see cref="ErrorTypes.FunctionExecutionNotFound"/>
    /// a nonexistent id would produce — never a distinguishable 401 — so this endpoint cannot be used
    /// to probe whether a given execution id exists, the same "don't confirm existence" instinct
    /// permission-filtered row reads already follow.
    ///
    /// A guest can never read anything back here: <see cref="CallerIdentity"/> returns null for an
    /// unauthenticated caller because unauthenticated callers are indistinguishable from each other —
    /// there is no "this guest" to match against a stored "guest" string, so a guest-triggered
    /// execution is unrecoverable through this endpoint by design, not by omission.
    /// </summary>
    private static async Task<IResult> GetDataPlaneExecution(
        string functionId, string executionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var principal = AppPrincipalFilter.Current(http);
        if (principal is RequestPrincipal.Key)
            RequireAnyScope(http, ApiKeyScopes.ExecutionRead, ApiKeyScopes.ExecutionWrite);

        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var execution = await FindExecutionAsync(functions, fn.Id, executionId, ct);

        // A bypass key, or one an operator explicitly trusted with execution.read, may read any
        // execution of this function — everyone else (app users, JWTs, and a key holding only
        // execution.write) is limited to the execution they themselves triggered.
        var hasBroadReadAccess = principal is RequestPrincipal.Key(var apiKey)
            && (apiKey.BypassRowPermissions || apiKey.Scopes.Contains(ApiKeyScopes.ExecutionRead));
        if (!hasBroadReadAccess && execution.TriggeredBy != CallerIdentity(http))
            throw PraxyException.NotFound(ErrorTypes.FunctionExecutionNotFound, "Execution not found.");

        return Results.Ok(FunctionExecutionResponse.From(execution));
    }

    /// <summary>
    /// The broad execution list Appwrite's <c>execution.read</c> implies — every execution of the
    /// function, not just the caller's own. Deliberately key-only (no app-user/JWT path): an app
    /// user listing every execution of a function it can merely invoke would be the same cross-caller
    /// leak <see cref="GetDataPlaneExecution"/>'s own-execution default exists to prevent. A key only
    /// gets here because an operator explicitly granted it this scope.
    /// </summary>
    private static async Task<IResult> ListDataPlaneExecutions(
        string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.ExecutionRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var (limit, offset) = ListParams(http);
        var (total, page) = await functions.ListExecutionsAsync(fn.Id, limit, offset, ct);
        return Results.Ok(new FunctionExecutionListResponse(total, [.. page.Select(FunctionExecutionResponse.From)]));
    }

    // ---- server-side function management (functions.read / functions.write keys) ----------------

    private static async Task<IResult> ServerListFunctions(HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var list = await functions.ListAsync(project.Id, ct);
        return Results.Ok(new FunctionListResponse(
            list.Count, [.. list.Select(f => FunctionResponse.From(f, functions.IsWarm(f)))]));
    }

    private static IResult ServerListRuntimes(HttpContext http, FunctionsOptions options)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        return Results.Ok(new FunctionRuntimeListResponse(
            [.. FunctionRuntimes.All.Select(r => new FunctionRuntimeResponse(
                r, r == FunctionRuntimes.Dart ? options.DartBaseImage : options.NodeBaseImage))]));
    }

    private static async Task<IResult> ServerCreateFunction(
        CreateFunctionRequest req, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await functions.CreateAsync(
            project.Id, req.Key, req.Name, req.Runtime, req.Entrypoint, req.TimeoutSeconds ?? 15, req.Events ?? [],
            req.Execute ?? [], req.Schedule, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.create", $"function/{Ids.Wire(fn.Id)}", ct);
        return Results.Created($"/v1/functions/{Ids.Wire(fn.Id)}", FunctionResponse.From(fn, false));
    }

    private static async Task<IResult> ServerGetFunction(
        string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        return Results.Ok(FunctionResponse.From(fn, functions.IsWarm(fn)));
    }

    private static async Task<IResult> ServerUpdateFunction(
        string functionId, UpdateFunctionRequest req, HttpContext http, PraxyDb db, FunctionsService functions,
        CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var updated = await functions.UpdateAsync(
            fn, req.Name, req.Entrypoint, req.TimeoutSeconds, req.Events, req.Execute, req.Schedule, req.Enabled,
            req.PlatformScopes, ct);
        await AuditKeyAsync(db, http, project.Id,
            PermissionAuditAction(req.Execute, req.PlatformScopes), $"function/{functionId}", ct);
        return Results.Ok(FunctionResponse.From(updated, functions.IsWarm(updated)));
    }

    private static async Task<IResult> ServerDeleteFunction(
        string functionId, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        await functions.DeleteAsync(fn, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.delete", $"function/{functionId}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ServerListEnvVars(
        string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var list = await functions.ListEnvVarsAsync(fn.Id, ct);
        return Results.Ok(new FunctionEnvVarListResponse(list.Count, [.. list.Select(FunctionEnvVarResponse.From)]));
    }

    private static async Task<IResult> ServerSetEnvVar(
        string functionId, string envKey, SetEnvVarRequest req, HttpContext http, PraxyDb db,
        FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var v = await functions.SetEnvVarAsync(fn.Id, envKey, req.Value, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.env.set", $"function/{functionId}/env/{envKey}", ct);
        return Results.Ok(FunctionEnvVarResponse.From(v));
    }

    private static async Task<IResult> ServerDeleteEnvVar(
        string functionId, string envKey, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        await functions.DeleteEnvVarAsync(fn.Id, envKey, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.env.delete", $"function/{functionId}/env/{envKey}", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ServerListDeployments(
        string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var list = await functions.ListDeploymentsAsync(fn.Id, ct);
        return Results.Ok(new FunctionDeploymentListResponse(list.Count, [.. list.Select(FunctionDeploymentResponse.From)]));
    }

    private static async Task<IResult> ServerCreateDeployment(
        string functionId, HttpContext http, PraxyDb db, FunctionsService functions, FunctionsOptions options,
        CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var tar = await ReadCappedAsync(http.Request.Body, options.MaxSourceBytes, ct);
        var deployment = await functions.CreateDeploymentAsync(fn, tar, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.deployments.create",
            $"function/{functionId}/deployment/{Ids.Wire(deployment.Id)}", ct);
        return Results.Created(
            $"/v1/functions/{functionId}/deployments/{Ids.Wire(deployment.Id)}", FunctionDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> ServerGetDeployment(
        string functionId, string deploymentId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsRead);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var deployment = await FindDeploymentAsync(functions, fn.Id, deploymentId, ct);
        return Results.Ok(FunctionDeploymentResponse.From(deployment));
    }

    private static async Task<IResult> ServerActivateDeployment(
        string functionId, string deploymentId, HttpContext http, PraxyDb db, FunctionsService functions, CancellationToken ct)
    {
        AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsWrite);
        var project = DataPlaneEndpoints.CurrentProject(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var deployment = await FindDeploymentAsync(functions, fn.Id, deploymentId, ct);
        var activated = await functions.ActivateAsync(fn, deployment, ct);
        await AuditKeyAsync(db, http, project.Id, "functions.deployments.activate",
            $"function/{functionId}/deployment/{deploymentId}", ct);
        return Results.Ok(FunctionDeploymentResponse.From(activated));
    }

    /// <summary>
    /// The wire identity of the calling app user or API key — exactly what
    /// <see cref="FunctionExecution.TriggeredBy"/> stores at invoke time, and what
    /// <see cref="GetDataPlaneExecution"/> checks a stored value against. Null for a guest: every
    /// unauthenticated caller is indistinguishable from every other, so there is no identity to
    /// record or to match later.
    /// </summary>
    private static string? CallerIdentity(HttpContext http) =>
        AppPrincipalFilter.Current(http) switch
        {
            RequestPrincipal.AppUser(var user, _) => $"user:{Ids.Wire(user.Id)}",
            RequestPrincipal.JwtUser(var user) => $"user:{Ids.Wire(user.Id)}",
            RequestPrincipal.Key(var apiKey) => $"key:{Ids.Wire(apiKey.Id)}",
            _ => null,
        };

    private static void RequireInvokable(FunctionDef fn)
    {
        if (!fn.Enabled)
            throw new PraxyException(400, ErrorTypes.FunctionDisabled, "This function is disabled.");
        if (fn.ActiveDeploymentId is null)
            throw new PraxyException(400, ErrorTypes.FunctionNoActiveDeployment, "This function has no active deployment yet.");
    }

    /// <summary>
    /// The data plane's authorization gate, mirroring <see cref="RowEndpoints"/>'s table-permission
    /// check: roles come from THE role resolver (roadmap rule 2), and a function with an empty
    /// <c>execute</c> list is reachable by nobody (rule 3). A key needs its <c>execution.write</c>
    /// scope <em>and</em> a matching role, exactly as a key needs both a <c>databases.*</c> scope and
    /// a table permission — except a <c>BypassRowPermissions</c> key, which is already the documented
    /// "trusted server, skip the permission layer" escape hatch on rows and means the same here.
    /// </summary>
    private static async Task RequireExecutePermissionAsync(HttpContext http, FunctionDef fn, IRoleResolver roleResolver)
    {
        if (AppPrincipalFilter.Current(http) is RequestPrincipal.Key(var apiKey) && apiKey.BypassRowPermissions)
            return;
        var roles = await RequestRoles.GetAsync(http, roleResolver);
        if (!FunctionsService.CanExecute(fn, roles))
            throw PraxyException.Unauthorized("Not permitted to execute this function.");
    }

    /// <summary>Passes if the current key holds any one of <paramref name="scopes"/>; else the same 401 <see cref="AppPrincipalFilter.RequireScope"/> gives for a single missing scope.</summary>
    private static void RequireAnyScope(HttpContext http, params string[] scopes)
    {
        if (AppPrincipalFilter.Current(http) is not RequestPrincipal.Key(var apiKey))
            throw PraxyException.Unauthorized("This endpoint requires an API key.");
        if (!scopes.Any(apiKey.Scopes.Contains))
            throw new PraxyException(401, ErrorTypes.GeneralUnauthorizedScope,
                $"The API key is missing one of: {string.Join(", ", scopes)}.");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<FunctionDef> FindAsync(FunctionsService functions, string projectId, string functionId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(functionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.FunctionNotFound, "Function not found.");
        return await functions.GetAsync(projectId, parsed, ct);
    }

    private static async Task<FunctionDeployment> FindDeploymentAsync(
        FunctionsService functions, Guid functionId, string deploymentId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(deploymentId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.FunctionDeploymentNotFound, "Deployment not found.");
        return await functions.GetDeploymentAsync(functionId, parsed, ct);
    }

    private static async Task<FunctionExecution> FindExecutionAsync(
        FunctionsService functions, Guid functionId, string executionId, CancellationToken ct)
    {
        if (!Ids.TryParseWire(executionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.FunctionExecutionNotFound, "Execution not found.");
        return await functions.GetExecutionAsync(functionId, parsed, ct);
    }

    private static (int Limit, int Offset) ListParams(HttpContext http)
    {
        var limit = int.TryParse(http.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
        var offset = int.TryParse(http.Request.Query["offset"], out var o) ? Math.Max(0, o) : 0;
        return (limit, offset);
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new PraxyException(400, ErrorTypes.FunctionInvalidSource,
                    $"Upload exceeds the {maxBytes / (1024 * 1024)}MB limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task AuditAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"admin:{op.Account.Id}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The server management surface's counterpart to <see cref="AuditAsync"/> — same action
    /// vocabulary, actor <c>key:&lt;id&gt;</c> instead of <c>admin:&lt;id&gt;</c>, following the
    /// precedent set on the users server surface (post-v0.1.0 gap #3): a functions.write key can
    /// create, redeploy, or delete a function exactly like an operator, so it gets the same trace.
    /// </summary>
    private static async Task AuditKeyAsync(
        PraxyDb db, HttpContext http, string projectId, string action, string resource, CancellationToken ct)
    {
        if (AppPrincipalFilter.Current(http) is not RequestPrincipal.Key(var apiKey))
            throw new InvalidOperationException("Server function-write endpoints require a key principal.");
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = projectId,
            Actor = $"key:{Ids.Wire(apiKey.Id)}",
            Action = action,
            Resource = resource,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ct);
    }
}

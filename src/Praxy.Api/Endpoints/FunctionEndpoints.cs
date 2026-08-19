using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Functions;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record CreateFunctionRequest(
    string Key, string Name, string Runtime, string Entrypoint, int? TimeoutSeconds, string[]? Events,
    string[]? Execute, string? Schedule);

public sealed record UpdateFunctionRequest(
    string? Name, string? Entrypoint, int? TimeoutSeconds, string[]? Events, string[]? Execute, string? Schedule,
    bool? Enabled);

public sealed record SetEnvVarRequest(string Value);

public sealed record InvokeFunctionRequest(string? Method, string? Path, string? Body);

public sealed record FunctionRuntimeResponse(string Id, string BaseImage);

public sealed record FunctionResponse(
    string Id, string Key, string Name, string Runtime, string Entrypoint, int TimeoutSeconds, bool Enabled,
    string[] Events, string[] Execute, string? Schedule, DateTimeOffset? NextScheduledRunAt,
    string? ActiveDeploymentId, bool IsWarm, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static FunctionResponse From(FunctionDef f, bool isWarm) => new(
        Ids.Wire(f.Id), f.Key, f.Name, f.Runtime, f.Entrypoint, f.TimeoutSeconds, f.Enabled, f.Events, f.Execute,
        f.Schedule, f.NextScheduledRunAt, f.ActiveDeploymentId is { } d ? Ids.Wire(d) : null, isWarm,
        f.CreatedAt, f.UpdatedAt);
}

public sealed record FunctionEnvVarResponse(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static FunctionEnvVarResponse From(FunctionEnvVar v) => new(v.Key, v.CreatedAt, v.UpdatedAt);
}

public sealed record FunctionDeploymentResponse(
    string Id, string Status, long SourceSizeBytes, string BuildLog, string? Error, string? ImageTag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ActivatedAt)
{
    public static FunctionDeploymentResponse From(FunctionDeployment d) => new(
        Ids.Wire(d.Id), d.Status, d.SourceSizeBytes, d.BuildLog, d.Error, d.ImageTag, d.CreatedAt, d.UpdatedAt, d.ActivatedAt);
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
/// </summary>
public static class FunctionEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/v1/console/projects/{projectId}/functions")
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ConsoleProjectFilter>();

        admin.MapGet("", ListFunctions);
        admin.MapGet("/runtimes", ListRuntimes);
        admin.MapPost("", CreateFunction);
        admin.MapGet("/{functionId}", GetFunction);
        admin.MapPatch("/{functionId}", UpdateFunction);
        admin.MapDelete("/{functionId}", DeleteFunction);

        admin.MapGet("/{functionId}/env", ListEnvVars);
        admin.MapPut("/{functionId}/env/{envKey}", SetEnvVar);
        admin.MapDelete("/{functionId}/env/{envKey}", DeleteEnvVar);

        admin.MapGet("/{functionId}/deployments", ListDeployments);
        admin.MapPost("/{functionId}/deployments", CreateDeployment);
        admin.MapGet("/{functionId}/deployments/{deploymentId}", GetDeployment);
        admin.MapPost("/{functionId}/deployments/{deploymentId}/activate", ActivateDeployment);

        admin.MapGet("/{functionId}/executions", ListExecutions);
        admin.MapGet("/{functionId}/executions/{executionId}", GetExecution);
        admin.MapPost("/{functionId}/executions", ConsoleInvoke);

        // Tighter than the rest of the data plane: every permitted request here can start a
        // container, so this is the one bucket where the limit is about capacity, not just abuse.
        var dataPlane = api.MapGroup("/v1/functions")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>()
            .RequireRateLimiting("functions");

        dataPlane.MapPost("/{functionId}/executions", Invoke);
    }

    // ---- functions ------------------------------------------------------------------------------

    private static async Task<IResult> ListFunctions(HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var list = await functions.ListAsync(project.Id, ct);
        return Results.Ok(new { total = list.Count, functions = list.Select(f => FunctionResponse.From(f, functions.IsWarm(f))) });
    }

    /// <summary>
    /// Base images are an operator config knob (<c>Praxy:Functions:DartBaseImage</c>/<c>NodeBaseImage</c>,
    /// self-host.md documents both), not a hardcoded constant — the console's runtime picker calls this
    /// instead of assuming the upstream defaults, so a self-hoster who pinned a different tag sees their
    /// actual pin, not a stale guess.
    /// </summary>
    private static IResult ListRuntimes(FunctionsOptions options) =>
        Results.Ok(new
        {
            runtimes = FunctionRuntimes.All.Select(r => new FunctionRuntimeResponse(
                r, r == FunctionRuntimes.Dart ? options.DartBaseImage : options.NodeBaseImage)),
        });

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
            fn, req.Name, req.Entrypoint, req.TimeoutSeconds, req.Events, req.Execute, req.Schedule, req.Enabled, ct);
        await AuditAsync(db, http, project.Id, "functions.update", $"function/{functionId}", ct);
        return Results.Ok(FunctionResponse.From(updated, functions.IsWarm(updated)));
    }

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
        return Results.Ok(new { total = list.Count, vars = list.Select(FunctionEnvVarResponse.From) });
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

    // ---- deployments ----------------------------------------------------------------------------

    private static async Task<IResult> ListDeployments(string functionId, HttpContext http, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var fn = await FindAsync(functions, project.Id, functionId, ct);
        var list = await functions.ListDeploymentsAsync(fn.Id, ct);
        return Results.Ok(new { total = list.Count, deployments = list.Select(FunctionDeploymentResponse.From) });
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
        return Results.Ok(new { total, executions = page.Select(FunctionExecutionResponse.From) });
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
            AppPrincipalFilter.RequireScope(http, ApiKeyScopes.FunctionsExecute);

        var project = DataPlaneEndpoints.CurrentProject(http);
        if (!Ids.TryParseWire(functionId, out var parsed))
            throw PraxyException.NotFound(ErrorTypes.FunctionNotFound, "Function not found.");
        var fn = await functions.GetAsync(project.Id, parsed, ct);
        // Authorize before reporting state: an unauthorized caller gets the same 401 whether or not
        // the function is disabled or undeployed, rather than being told which.
        await RequireExecutePermissionAsync(http, fn, roleResolver);
        RequireInvokable(fn);

        var isAsync = bool.TryParse(http.Request.Query["async"], out var a) && a;
        var triggeredBy = AppPrincipalFilter.Current(http) switch
        {
            RequestPrincipal.AppUser(var user, _) => $"user:{Ids.Wire(user.Id)}",
            RequestPrincipal.JwtUser(var user) => $"user:{Ids.Wire(user.Id)}",
            RequestPrincipal.Key => "key",
            _ => "guest",
        };
        var execution = await functions.CreateExecutionAsync(
            fn, "http", isAsync, req.Method ?? "GET", req.Path ?? "/", req.Body, triggeredBy, ct);

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
    /// <c>execute</c> list is reachable by nobody (rule 3). A key needs its <c>functions.execute</c>
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
}

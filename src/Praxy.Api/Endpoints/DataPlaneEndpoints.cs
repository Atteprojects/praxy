using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record PingResponse(string Status, string ProjectId, DateTimeOffset At);

/// <summary>Liveness. Deliberately free of the error envelope — load balancers poll it.</summary>
public sealed record HealthResponse(string Status, string Version);

/// <summary>
/// The data plane — everything an app (as opposed to a console operator) calls. Every endpoint
/// in this group resolves <c>X-Praxy-Project</c> and hard-refuses the reserved console project.
/// </summary>
public static class DataPlaneEndpoints
{
    public const string ProjectHeader = "X-Praxy-Project";
    private const string ItemKey = "praxy.project";

    public static void Map(IEndpointRouteBuilder api)
    {
        var dataPlane = api.MapGroup("/v1")
            .AddEndpointFilter<ProjectGuardFilter>();

        // The first real SDK/API call a new project sees. Stamps last_ping_at, which the
        // console overview polls to advance its onboarding state.
        dataPlane.MapGet("/ping", async (HttpContext http, PraxyDb db, CancellationToken ct) =>
        {
            var project = CurrentProject(http);
            var now = DateTimeOffset.UtcNow;
            await db.Projects.Where(p => p.Id == project.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastPingAt, now), ct);
            return Results.Ok(new PingResponse("pong", project.Id, now));
        }).Produces<PingResponse>();
    }

    public static Project CurrentProject(HttpContext http) =>
        http.Items[ItemKey] as Project
        ?? throw new InvalidOperationException("Project context missing — endpoint not behind ProjectGuardFilter.");

    /// <summary>
    /// Resolves the project header for data-plane calls. The reserved console project is
    /// refused with a stable, tested error — operator data is unreachable from the data plane.
    /// </summary>
    public sealed class ProjectGuardFilter(PraxyDb db) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            var http = ctx.HttpContext;
            var projectId = http.Request.Headers[ProjectHeader].FirstOrDefault();
            if (string.IsNullOrEmpty(projectId))
                throw PraxyException.ArgumentInvalid($"The {ProjectHeader} header is required.",
                    new Dictionary<string, string[]> { ["project"] = ["Missing project id."] });

            if (Ids.IsReservedProjectId(projectId))
                throw new PraxyException(403, ErrorTypes.ProjectReserved,
                    "The console project is not accessible through the data-plane API.");

            var project = await db.Projects
                .FirstOrDefaultAsync(p => p.Id == projectId, http.RequestAborted)
                ?? throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");

            http.Items[ItemKey] = project;
            return await next(ctx);
        }
    }
}

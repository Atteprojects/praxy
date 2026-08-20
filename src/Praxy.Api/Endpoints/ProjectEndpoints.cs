using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Functions;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Tables;
using Praxy.Tables.Quotas;

namespace Praxy.Api.Endpoints;

public sealed record CreateProjectRequest(string Name, string? ProjectId);

public sealed record UpdateProjectRequest(string Name);

public sealed record ProjectResponse(
    string Id, string Name, string? OrganizationId, DateTimeOffset? LastPingAt, DateTimeOffset CreatedAt)
{
    // organizationId is hex32 like every other uuid-keyed id on the wire (it used to leak the raw
    // dashed Guid, the one exception in the API). Null only for the reserved console project,
    // which no operator can see anyway.
    public static ProjectResponse From(Project p) =>
        new(p.Id, p.Name, p.OrganizationId is { } org ? Ids.Wire(org) : null, p.LastPingAt, p.CreatedAt);
}

public sealed record ProjectListResponse(int Total, IReadOnlyList<ProjectResponse> Projects);

public static class ProjectEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var projects = api.MapGroup("/v1/console/projects")
            .AddEndpointFilter<RequireOperatorFilter>();

        projects.MapGet("", List).Produces<ProjectListResponse>();
        projects.MapPost("", Create).Produces<ProjectResponse>(StatusCodes.Status201Created);
        projects.MapGet("/{projectId}", Get).Produces<ProjectResponse>();
        projects.MapPatch("/{projectId}", Update)
            .AddEndpointFilter<ConsoleProjectFilter>()
            .Produces<ProjectResponse>();
        projects.MapDelete("/{projectId}", Delete)
            .AddEndpointFilter<ConsoleProjectFilter>()
            .Produces(StatusCodes.Status204NoContent);
        projects.MapGet("/{projectId}/quotas", GetQuotas)
            .AddEndpointFilter<ConsoleProjectFilter>()
            .Produces<QuotaSnapshot>();
    }

    private static async Task<IResult> List(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var list = await AccessibleProjects(db, op.Account.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new ProjectListResponse(list.Count, [.. list.Select(ProjectResponse.From)]));
    }

    private static async Task<IResult> Create(
        CreateProjectRequest req, HttpContext http, PraxyDb db, QuotaService quotas, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);

        var name = req.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid project payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });

        string id;
        if (!string.IsNullOrEmpty(req.ProjectId))
        {
            id = req.ProjectId;
            if (Ids.IsReservedProjectId(id))
                throw new PraxyException(400, ErrorTypes.ProjectReserved, $"The project id '{Ids.ConsoleProjectId}' is reserved.");
            if (!Ids.IsValidCustomId(id))
                throw new PraxyException(400, ErrorTypes.ProjectInvalidId,
                    "Project ids are 1-36 lowercase letters, digits or hyphens, starting with a letter or digit.");
            if (await db.Projects.AnyAsync(p => p.Id == id, ct))
                throw new PraxyException(409, ErrorTypes.ProjectAlreadyExists, $"A project with id '{id}' already exists.");
        }
        else
        {
            id = Ids.NewResourceId();
        }

        // Single-org world for now: every project lands in the operator's silently created org.
        var orgId = await db.OrganizationMembers
            .Where(m => m.UserId == op.Account.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct)
            ?? throw new PraxyException(500, ErrorTypes.GeneralServerError, "Operator has no organization.");

        await quotas.EnsureProjectQuotaAsync(orgId, ct);

        var project = new Project { Id = id, OrganizationId = orgId, Name = name };
        db.Projects.Add(project);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = project.Id,
            Actor = $"admin:{op.Account.Id}",
            Action = "projects.create",
            Resource = $"project/{project.Id}",
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new PraxyException(409, ErrorTypes.ProjectAlreadyExists, $"A project with id '{id}' already exists.");
        }

        return Results.Created($"/v1/console/projects/{project.Id}", ProjectResponse.From(project));
    }

    private static async Task<IResult> Get(string projectId, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var project = await AccessibleProjects(db, op.Account.Id)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");
        return Results.Ok(ProjectResponse.From(project));
    }

    /// <summary>Name only — <see cref="Project.Id"/> is the wire-visible id, chosen at creation, and never changes.</summary>
    private static async Task<IResult> Update(
        UpdateProjectRequest req, HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);

        var name = req.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 128)
            throw PraxyException.ArgumentInvalid("Invalid project payload.",
                new Dictionary<string, string[]> { ["name"] = ["Must be between 1 and 128 characters."] });
        project.Name = name;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        await AuditAsync(db, http, project.Id, "projects.update", $"project/{project.Id}", ct);
        return Results.Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// The single most destructive console operation, so it gets every guard the codebase has:
    /// force-gated like every other destructive delete, and physical Postgres schemas plus running
    /// function containers are torn down explicitly first — neither has an FK relationship to the
    /// project row, so <c>db.Projects.Remove(project)</c>'s cascade would silently orphan them on
    /// disk (schemas) or leave them running with no database row to ever evict them (containers).
    ///
    /// Runs as several transactions, not one: <see cref="DatabasesService.DeleteAsync"/> and
    /// <see cref="FunctionsService.DeleteAsync"/> already each atomically commit their own
    /// metadata-plus-DDL (or metadata-plus-container-evict) work, one resource at a time. Wrapping
    /// all of that plus the final project-row delete in one outer transaction would mean one lock
    /// spanning every `DROP SCHEMA` in the project — and worse, a delete interrupted partway
    /// through would leave nothing retryable, since the whole thing rolls back together. Several
    /// transactions means an interrupted delete is safely resumable: call DELETE again with
    /// force=true, and the databases/functions already removed simply no longer appear in the list
    /// this loops over.
    /// </summary>
    private static async Task<IResult> Delete(
        HttpContext http, PraxyDb db, DatabasesService databases, FunctionsService functions, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        if (!SchemaLookup.TryParseForce(http))
            throw new PraxyException(400, ErrorTypes.GeneralForceRequired,
                "Deleting a project removes every database, function, user, key and team it contains. Pass force=true to confirm.");

        foreach (var database in await databases.ListAsync(project.Id, ct))
            await databases.DeleteAsync(database, force: true, ct);
        foreach (var fn in await functions.ListAsync(project.Id, ct))
            await functions.DeleteAsync(fn, ct);

        var op = RequireOperatorFilter.Current(http);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            // Instance-level, not project-scoped: once this row is gone, GET .../projects/{id}/audit
            // can never resolve the project again to serve it, but GET /v1/console/audit still can.
            ProjectId = null,
            Actor = $"admin:{op.Account.Id}",
            Action = "projects.delete",
            Resource = $"project/{project.Id}",
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        });
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Org-level quotas reached through a project (roadmap Phase 9): this project's usage against
    /// the effective limits (org override, else instance default). The console shows the owning
    /// organization on its home screen, but quotas still have no org-id entry point.
    /// </summary>
    private static async Task<IResult> GetQuotas(HttpContext http, QuotaService quotas, CancellationToken ct)
    {
        var project = ConsoleProjectFilter.Current(http);
        var snapshot = await quotas.GetSnapshotAsync(project.Id, ct);
        return Results.Ok(snapshot);
    }

    /// <summary>
    /// Projects in organizations the operator belongs to. The console project has no
    /// organization, so it can never appear here.
    /// </summary>
    private static IQueryable<Project> AccessibleProjects(PraxyDb db, Guid operatorId) =>
        from p in db.Projects
        join m in db.OrganizationMembers on p.OrganizationId equals m.OrganizationId
        where m.UserId == operatorId
        select p;

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

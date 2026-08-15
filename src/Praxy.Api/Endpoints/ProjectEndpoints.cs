using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

public sealed record CreateProjectRequest(string Name, string? ProjectId);

public sealed record ProjectResponse(
    string Id, string Name, Guid? OrganizationId, DateTimeOffset? LastPingAt, DateTimeOffset CreatedAt)
{
    public static ProjectResponse From(Project p) =>
        new(p.Id, p.Name, p.OrganizationId, p.LastPingAt, p.CreatedAt);
}

public static class ProjectEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var projects = api.MapGroup("/v1/console/projects")
            .AddEndpointFilter<RequireOperatorFilter>();

        projects.MapGet("", List);
        projects.MapPost("", Create);
        projects.MapGet("/{projectId}", Get);
    }

    private static async Task<IResult> List(HttpContext http, PraxyDb db, CancellationToken ct)
    {
        var op = RequireOperatorFilter.Current(http);
        var list = await AccessibleProjects(db, op.Account.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new { total = list.Count, projects = list.Select(ProjectResponse.From) });
    }

    private static async Task<IResult> Create(
        CreateProjectRequest req, HttpContext http, PraxyDb db, CancellationToken ct)
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

        var project = new Project { Id = id, OrganizationId = orgId, Name = name };
        db.Projects.Add(project);
        db.AuditLog.Add(new AuditLogEntry
        {
            Id = Ids.NewUuid(),
            ProjectId = project.Id,
            Actor = $"user:{op.Account.Id}",
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

    /// <summary>
    /// Projects in organizations the operator belongs to. The console project has no
    /// organization, so it can never appear here.
    /// </summary>
    private static IQueryable<Project> AccessibleProjects(PraxyDb db, Guid operatorId) =>
        from p in db.Projects
        join m in db.OrganizationMembers on p.OrganizationId equals m.OrganizationId
        where m.UserId == operatorId
        select p;
}

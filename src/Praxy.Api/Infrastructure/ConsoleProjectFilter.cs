using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Infrastructure;

/// <summary>
/// Scopes console admin endpoints (<c>/v1/console/projects/{projectId}/…</c>) to a project the
/// signed-in operator can actually reach: the id must resolve, must not be the reserved console
/// project, and must belong to one of the operator's organizations. Runs after
/// <see cref="RequireOperatorFilter"/>.
/// </summary>
public sealed class ConsoleProjectFilter(PraxyDb db) : IEndpointFilter
{
    private const string ItemKey = "praxy.console_project";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var op = RequireOperatorFilter.Current(http);
        var projectId = http.Request.RouteValues["projectId"] as string;
        if (string.IsNullOrEmpty(projectId) || Ids.IsReservedProjectId(projectId))
            throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");

        var project = await (
                from p in db.Projects
                join m in db.OrganizationMembers on p.OrganizationId equals m.OrganizationId
                where m.UserId == op.Account.Id && p.Id == projectId
                select p)
            .FirstOrDefaultAsync(http.RequestAborted)
            ?? throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");

        http.Items[ItemKey] = project;
        return await next(ctx);
    }

    public static Project Current(HttpContext http) =>
        http.Items[ItemKey] as Project
        ?? throw new InvalidOperationException("Console project missing — endpoint not behind ConsoleProjectFilter.");
}

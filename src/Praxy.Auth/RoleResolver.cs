using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

/// <summary>Who is calling: a guest, an app user with a session, or a server holding an API key.</summary>
public abstract record RequestPrincipal
{
    public sealed record Guest : RequestPrincipal;

    public sealed record AppUser(User User, Session Session) : RequestPrincipal;

    public sealed record Key(ApiKey ApiKey) : RequestPrincipal;

    /// <summary>
    /// A caller authenticated with a stateless account JWT (Phase 7, <c>AccountJwtService</c>) rather
    /// than a real session — the shape a function invocation's scoped user JWT resolves to when it
    /// calls back into the API. Resolves the same roles as <see cref="AppUser"/> but satisfies none
    /// of the session-specific account endpoints (no <see cref="Session"/> to reference).
    /// </summary>
    public sealed record JwtUser(User User) : RequestPrincipal;
}

/// <summary>
/// THE role resolver. One implementation, consumed everywhere permissions are decided — the
/// query compiler (Phase 3) and the realtime fan-out (Phase 4) get this same list. Never fork it.
/// </summary>
public interface IRoleResolver
{
    Task<string[]> ResolveAsync(RequestPrincipal principal, CancellationToken ct = default);
}

public sealed class RoleResolver(PraxyDb db) : IRoleResolver
{
    public async Task<string[]> ResolveAsync(RequestPrincipal principal, CancellationToken ct = default)
    {
        switch (principal)
        {
            case RequestPrincipal.Guest:
                return ["any", "guests"];

            case RequestPrincipal.Key:
                // API keys authorize through scopes, not roles. They still satisfy `any`;
                // whether they bypass row permissions is an explicit flag decided in Phase 3.
                return ["any"];

            case RequestPrincipal.AppUser(var user, _):
                return await ResolveUserRolesAsync(user, ct);

            case RequestPrincipal.JwtUser(var user):
                return await ResolveUserRolesAsync(user, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(principal));
        }
    }

    private async Task<string[]> ResolveUserRolesAsync(User user, CancellationToken ct)
    {
        var id = Ids.Wire(user.Id);
        var roles = new List<string> { "any", "users" };
        if (user.EmailVerified)
            roles.Add("users/verified");
        roles.Add($"user:{id}");
        if (user.EmailVerified)
            roles.Add($"user:{id}/verified");

        var memberships = await db.Memberships
            .Where(m => m.UserId == user.Id && m.Confirmed)
            .Select(m => new { m.Id, m.TeamId, m.Roles })
            .ToListAsync(ct);
        foreach (var m in memberships)
        {
            roles.Add($"member:{Ids.Wire(m.Id)}");
            roles.Add($"team:{Ids.Wire(m.TeamId)}");
            foreach (var role in m.Roles)
                roles.Add($"team:{Ids.Wire(m.TeamId)}/{role}");
        }

        foreach (var label in user.Labels)
            roles.Add($"label:{label}");

        return [.. roles];
    }
}

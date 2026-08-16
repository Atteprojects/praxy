using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Core;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Auth;

/// <summary>
/// Mints and verifies short-lived, stateless "account JWTs" — <c>POST /account/jwts</c>
/// (research/appwrite-api.md: "Phase 1 optional, Phase 7 required"). Reuses <see cref="CompactJwt"/>
/// and <see cref="InstanceKey"/>'s signing key exactly as <c>OAuthService</c> already does for the
/// OAuth state cookie and callback secret wrap — no new signing mechanism.
/// </summary>
/// <remarks>
/// Deliberately stateless: unlike a session, a JWT carries no row to revoke, so deleting the
/// session it was minted from does not retroactively invalidate it — the same tradeoff Appwrite's
/// own account JWTs make, and the reason the lifetime is capped short rather than left open-ended
/// (<see cref="MaxLifetime"/>, configurable per CLAUDE.md's "every limit is configurable" rule).
/// A JWT authenticates as <see cref="RequestPrincipal.JwtUser"/> — enough for the query
/// compiler/permission engine to resolve roles for it (what Phase 7 function invocations need to
/// "act as a specific user"), but deliberately not accepted by <c>AppPrincipalFilter.RequireUser</c>
/// — session-management endpoints (list/delete sessions, change password) still require a real session.
/// </remarks>
public sealed class AccountJwtService(PraxyDb db, InstanceKey key)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public string Mint(string projectId, Guid userId, TimeSpan lifetime) =>
        CompactJwt.Encode(key.SigningKey, new JsonObject
        {
            ["projectId"] = projectId,
            ["userId"] = Ids.Wire(userId),
        }, lifetime);

    /// <summary>Null when the JWT is missing, malformed, expired, project-mismatched, or its user no longer exists/is blocked.</summary>
    public async Task<User?> VerifyAsync(string projectId, string jwt, CancellationToken ct = default)
    {
        var claims = CompactJwt.Decode(key.SigningKey, jwt);
        if (claims?["projectId"]?.GetValue<string>() != projectId)
            return null;
        if (claims["userId"]?.GetValue<string>() is not { } wireId || !Ids.TryParseWire(wireId, out var userId))
            return null;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.ProjectId == projectId, ct);
        return user is { Status: true } ? user : null;
    }
}

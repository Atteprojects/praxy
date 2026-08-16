using System.Text.Json.Nodes;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Persistence.Entities;

namespace Praxy.Api.Endpoints;

/// <summary>App-user wire shape. Ids are 32-hex-char uuids — the same form permission roles use.</summary>
public sealed record AppUserResponse(
    string Id, string Email, string Name, bool EmailVerified, bool Status, string[] Labels,
    JsonNode? Prefs, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static AppUserResponse From(User u) => new(
        Ids.Wire(u.Id), u.Email, u.Name, u.EmailVerified, u.Status, u.Labels,
        ParseOrEmpty(u.Prefs), u.CreatedAt, u.UpdatedAt);

    private static JsonNode ParseOrEmpty(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }
}

public sealed record SessionResponse(
    string Id, string UserId, string Provider, string? Ip, string? UserAgent, bool Current,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt)
{
    public static SessionResponse From(Session s, Guid? currentSessionId = null) => new(
        Ids.Wire(s.Id), Ids.Wire(s.UserId), s.Provider, s.Ip, s.UserAgent,
        s.Id == currentSessionId, s.ExpiresAt, s.CreatedAt);
}

/// <summary>Login/signup/exchange responses carry the opaque token once; storage is the SDK's job.</summary>
public sealed record CreatedSessionResponse(AppUserResponse User, SessionResponse Session, string Token)
{
    public static CreatedSessionResponse From(AppSession s) =>
        new(AppUserResponse.From(s.User), SessionResponse.From(s.Session, s.Session.Id), s.Token);
}

public sealed record TeamResponse(string Id, string Name, int MemberCount, DateTimeOffset CreatedAt)
{
    public static TeamResponse From(Team t, int memberCount) =>
        new(Ids.Wire(t.Id), t.Name, memberCount, t.CreatedAt);
}

public sealed record MembershipResponse(
    string Id, string TeamId, string UserId, string UserEmail, string UserName, string[] Roles,
    bool Confirmed, DateTimeOffset? InvitedAt, DateTimeOffset? JoinedAt)
{
    public static MembershipResponse From(MembershipWithUser m) => new(
        Ids.Wire(m.Membership.Id), Ids.Wire(m.Membership.TeamId), Ids.Wire(m.User.Id),
        m.User.Email, m.User.Name, m.Membership.Roles, m.Membership.Confirmed,
        m.Membership.InvitedAt, m.Membership.JoinedAt);
}

public sealed record ApiKeyResponse(
    string Id, string Name, string[] Scopes, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt,
    bool BypassRowPermissions, DateTimeOffset CreatedAt)
{
    public static ApiKeyResponse From(ApiKey k) =>
        new(Ids.Wire(k.Id), k.Name, k.Scopes, k.ExpiresAt, k.LastUsedAt, k.BypassRowPermissions, k.CreatedAt);
}

public sealed record PlatformResponse(string Id, string Type, string Name, string? Hostname, DateTimeOffset CreatedAt)
{
    public static PlatformResponse From(Platform p) =>
        new(Ids.Wire(p.Id), p.Type, p.Name, p.Hostname, p.CreatedAt);
}

/// <summary>Auth settings as the console sees them. The Google client secret is write-only — never echoed.</summary>
public sealed record AuthSettingsResponse(
    bool EmailPassword, bool GoogleEnabled, string? GoogleClientId, bool GoogleClientSecretSet,
    int SessionLimit, int PasswordMinLength)
{
    public static AuthSettingsResponse From(ProjectAuthSettings s) => new(
        s.EmailPassword, s.GoogleEnabled, s.GoogleClientId,
        !string.IsNullOrEmpty(s.GoogleClientSecret), s.SessionLimit, s.PasswordMinLength);
}

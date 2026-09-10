// GENERATED — do not edit by hand.
//
// Produced by `node sdk/generator/bin/generate.mjs` from docs/openapi/v1.json. Edit the API's
// endpoint definitions (or the generator) and regenerate; CI fails the build if this file and the
// document disagree.

using System.Text.Json.Serialization;

namespace Praxy.Sdk.Services.Generated;

/// <summary>
/// <para>Server-side app-user administration (`/v1/users`) — the surface an API key reaches and an end-user session never should. Create, inspect, update and delete a project's app users, and revoke their sessions.</para>
/// <para>Requires a client authenticated with an API key; every method here needs the `users.read`/`users.write` key scopes.</para>
/// <para>GENERATED from the OpenAPI document — see sdk/generator.</para>
/// </summary>
public sealed class UsersService(PraxyClient client)
{
    /// <summary>Creates an app user directly, without the sign-up flow or an email verification step.</summary>
    public Task<AppUser?> Create(string email, string? password = null, string? name = null, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Post, "/v1/users", body: new Dictionary<string, object?> { ["email"] = email, ["password"] = password, ["name"] = name }, cancellationToken: cancellationToken);

    /// <summary>Deletes an app user and everything scoped to them, including their sessions.</summary>
    public async Task Delete(string userId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<object>(HttpMethod.Delete, $"/v1/users/{userId}", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes every session an app user holds, signing them out everywhere.</summary>
    public async Task DeleteAllSessions(string userId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<object>(HttpMethod.Delete, $"/v1/users/{userId}/sessions", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes one of an app user's sessions.</summary>
    public async Task DeleteSession(string userId, string sessionId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<object>(HttpMethod.Delete, $"/v1/users/{userId}/sessions/{sessionId}", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches one app user by id.</summary>
    public Task<AppUser?> Get(string userId, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Get, $"/v1/users/{userId}", cancellationToken: cancellationToken);

    /// <summary>Lists the project's app users, newest first.</summary>
    public Task<AppUserList?> List(int? limit = null, int? offset = null, string? search = null, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUserList>(HttpMethod.Get, "/v1/users", body: null, query: new Dictionary<string, string?> { ["limit"] = limit?.ToString(System.Globalization.CultureInfo.InvariantCulture), ["offset"] = offset?.ToString(System.Globalization.CultureInfo.InvariantCulture), ["search"] = search }, cancellationToken: cancellationToken);

    /// <summary>Lists an app user's active sessions.</summary>
    public Task<SessionList?> ListSessions(string userId, CancellationToken cancellationToken = default) =>
        client.SendAsync<SessionList>(HttpMethod.Get, $"/v1/users/{userId}/sessions", cancellationToken: cancellationToken);

    /// <summary>Mirrors the console's change-email: the address moves and verified-ness resets with it. A collision inside the project is the existing user_already_exists, not a 500.</summary>
    public Task<AppUser?> UpdateEmail(string userId, string email, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/email", body: new Dictionary<string, object?> { ["email"] = email }, cancellationToken: cancellationToken);

    /// <summary>Replaces an app user's labels wholesale. Labels are the operator-assigned strings the permission engine can grant roles from.</summary>
    public Task<AppUser?> UpdateLabels(string userId, IReadOnlyList<string> labels, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/labels", body: new Dictionary<string, object?> { ["labels"] = labels }, cancellationToken: cancellationToken);

    /// <summary>Changes an app user's display name.</summary>
    public Task<AppUser?> UpdateName(string userId, string name, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/name", body: new Dictionary<string, object?> { ["name"] = name }, cancellationToken: cancellationToken);

    /// <summary>Sets a password without the old one — and revokes every session, as the console does.</summary>
    public Task<AppUser?> UpdatePassword(string userId, string password, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/password", body: new Dictionary<string, object?> { ["password"] = password }, cancellationToken: cancellationToken);

    /// <summary>Enables or disables an app user. A disabled user keeps their data but cannot authenticate.</summary>
    public Task<AppUser?> UpdateStatus(string userId, bool status, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/status", body: new Dictionary<string, object?> { ["status"] = status }, cancellationToken: cancellationToken);

    /// <summary>Marks an app user's email verified or unverified without sending them anything.</summary>
    public Task<AppUser?> UpdateVerification(string userId, bool emailVerified, CancellationToken cancellationToken = default) =>
        client.SendAsync<AppUser>(HttpMethod.Patch, $"/v1/users/{userId}/verification", body: new Dictionary<string, object?> { ["emailVerified"] = emailVerified }, cancellationToken: cancellationToken);
}

/// <summary>Wire shape of `AppUserListResponse`.</summary>
public sealed record AppUserList(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("users")] IReadOnlyList<AppUser> Users);

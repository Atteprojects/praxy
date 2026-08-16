using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core;
using Praxy.Core.Errors;
using Praxy.Persistence;
using Praxy.Persistence.Entities;
using Praxy.Realtime;

namespace Praxy.Api.Endpoints;

/// <summary>
/// <c>GET /v1/realtime?project=&lt;id&gt;(&amp;ticket=&lt;t&gt;)</c> — the WebSocket endpoint
/// (architecture.md §6), and <c>POST /v1/realtime/ticket</c> — the single-use ticket mint for
/// non-browser clients. The caller's principal and roles are fully resolved *before* the socket
/// is accepted (every input — cookie, ticket, key header — is already on the HTTP request), which
/// is what makes research/appwrite-api.md's "early subscribe before auth settles" race structural-
/// ly impossible here rather than something to detect and queue around.
/// </summary>
public static class RealtimeEndpoints
{
    private const int MaxMessageBytes = 64 * 1024;

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/v1/realtime", HandleSocket);

        var ticket = api.MapGroup("/v1/realtime")
            .AddEndpointFilter<DataPlaneEndpoints.ProjectGuardFilter>()
            .AddEndpointFilter<AppPrincipalFilter>();
        ticket.MapPost("/ticket", CreateTicket);
    }

    // ---- ticket mint ----------------------------------------------------------------------------

    private static async Task<IResult> CreateTicket(HttpContext http, TicketStore tickets)
    {
        var project = DataPlaneEndpoints.CurrentProject(http);
        var data = AppPrincipalFilter.Current(http) switch
        {
            RequestPrincipal.AppUser(var user, var session) => new RealtimeTicketData(project.Id, user.Id, session.Id, null),
            RequestPrincipal.Key(var key) => new RealtimeTicketData(project.Id, null, null, key.Id),
            _ => throw PraxyException.Unauthorized("Realtime tickets require a session or API key."),
        };
        var (value, expiresAt) = tickets.Mint(data);
        return Results.Ok(new { ticket = value, expiresAt });
    }

    // ---- socket -----------------------------------------------------------------------------------

    private static async Task HandleSocket(
        HttpContext http, PraxyDb db, AppAuthService appAuth, ApiKeyService apiKeys, ConsoleAuthService consoleAuth,
        IRoleResolver roleResolver, TicketStore tickets, ConnectionRegistry registry, RealtimeOptions options)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var projectId = http.Request.Query["project"].FirstOrDefault();
        if (string.IsNullOrEmpty(projectId) || Ids.IsReservedProjectId(projectId))
            throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, http.RequestAborted)
            ?? throw PraxyException.NotFound(ErrorTypes.ProjectNotFound, $"Project '{projectId}' not found.");

        var caller = await ResolveCallerAsync(http, project, db, appAuth, apiKeys, consoleAuth, tickets, roleResolver, http.RequestAborted);

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(20),
        });

        var connection = new Connection
        {
            ProjectId = project.Id,
            UserId = caller.UserId,
            SessionId = caller.SessionId,
            Bypass = caller.Bypass,
            Roles = caller.Roles,
        };

        if (!registry.TryRegister(connection, options.MaxConnectionsPerProject))
        {
            await TryCloseAsync(socket, RealtimeCloseCodes.Overloaded,
                $"Connection quota ({options.MaxConnectionsPerProject}) exceeded for project '{project.Id}'.", http.RequestAborted);
            return;
        }

        var ctx = new ConnCtx(connection, caller.Principal, roleResolver, registry, db);
        var writerTask = WriteLoopAsync(socket, connection);
        try
        {
            connection.Enqueue(ServerMessage.Connected(caller.UserNode));
            await ReadLoopAsync(socket, ctx);
        }
        finally
        {
            registry.Unregister(connection);
            connection.Outbound.Writer.TryComplete();
            try { await writerTask; } catch { /* the socket's own state is authoritative below */ }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var status = connection.PendingCloseStatus ?? WebSocketCloseStatus.NormalClosure;
                var reason = connection.PendingCloseReason ?? "Closed.";
                await TryCloseAsync(socket, status, reason, CancellationToken.None);
            }
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try { await socket.CloseAsync(status, reason, ct); }
        catch { /* best-effort — the peer may already be gone */ }
    }

    // ---- read / write loops -------------------------------------------------------------------

    private static async Task WriteLoopAsync(WebSocket socket, Connection connection)
    {
        try
        {
            await foreach (var message in connection.Outbound.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (socket.State != WebSocketState.Open)
                    break;
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { } // peer vanished mid-send
    }

    private static async Task ReadLoopAsync(WebSocket socket, ConnCtx ctx)
    {
        var buffer = new byte[8192];
        using var messageStream = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, ctx.Connection.CloseCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (messageStream.Length + result.Count > MaxMessageBytes)
                    {
                        ctx.Connection.RequestClose(RealtimeCloseCodes.BadMessage, "Message too large.");
                        return;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                return; // a hub- or overflow-requested close cancelled CloseCts
            }
            catch (WebSocketException)
            {
                return; // peer vanished
            }
            catch (IOException)
            {
                return; // peer vanished mid-frame
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                ctx.Connection.RequestClose(RealtimeCloseCodes.BadMessage, "Only text frames are supported.");
                return;
            }

            try
            {
                HandleMessage(ctx, messageStream.GetBuffer().AsSpan(0, (int)messageStream.Length));
            }
            catch (FormatException ex)
            {
                ctx.Connection.Enqueue(ServerMessage.Error(ErrorTypes.GeneralArgumentInvalid, ex.Message));
                ctx.Connection.RequestClose(RealtimeCloseCodes.BadMessage, ex.Message);
                return;
            }

            if (ctx.Connection.NeedsRevalidation)
                await RevalidateAsync(ctx, ctx.Connection.CloseCts.Token);
        }
    }

    private static void HandleMessage(ConnCtx ctx, ReadOnlySpan<byte> raw)
    {
        var (type, data) = ClientMessage.Parse(raw);
        switch (type)
        {
            case "ping":
                ctx.Connection.Enqueue(ServerMessage.Pong());
                break;

            case "subscribe":
            {
                var entries = ClientMessage.ParseSubscribe(data);
                foreach (var entry in entries)
                    ctx.Connection.Subscriptions[entry.SubscriptionId] =
                        ChannelGrammar.NormalizeForSubscribe(entry.Channels, ctx.Connection.UserId);
                ctx.Registry.Reindex(ctx.Connection);
                ctx.Connection.Enqueue(ServerMessage.Response("subscribe", entries.Select(e => e.SubscriptionId)));
                break;
            }

            case "unsubscribe":
            {
                var ids = ClientMessage.ParseUnsubscribe(data);
                foreach (var id in ids)
                    ctx.Connection.Subscriptions.Remove(id);
                ctx.Registry.Reindex(ctx.Connection);
                ctx.Connection.Enqueue(ServerMessage.Response("unsubscribe", ids));
                break;
            }

            default:
                throw new FormatException($"Unknown message type '{type}'.");
        }
    }

    /// <summary>
    /// Re-resolves roles for a connection <see cref="RealtimeHub"/> flagged dirty (a user/team/
    /// membership event touched one of its roles). Re-fetches the user row fresh — <see cref="IRoleResolver"/>
    /// reads labels/verification off the passed-in entity rather than the database, so reusing the
    /// stale one captured at connect time would make label/status revalidation silently inert.
    /// </summary>
    private static async Task RevalidateAsync(ConnCtx ctx, CancellationToken ct)
    {
        ctx.Connection.NeedsRevalidation = false;
        if (ctx.Principal is not RequestPrincipal principal)
            return;

        if (principal is RequestPrincipal.AppUser(_, var session))
        {
            var fresh = await ctx.Db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId, ct);
            if (fresh is null)
                return;
            principal = new RequestPrincipal.AppUser(fresh, session);
        }

        ctx.Connection.Roles = await ctx.RoleResolver.ResolveAsync(principal, ct);
        ctx.Registry.Reindex(ctx.Connection);
    }

    // ---- caller resolution --------------------------------------------------------------------

    private sealed record ConnCtx(Connection Connection, RequestPrincipal? Principal, IRoleResolver RoleResolver, ConnectionRegistry Registry, PraxyDb Db);

    private sealed record ResolvedCaller(RequestPrincipal? Principal, string[] Roles, bool Bypass, Guid? UserId, Guid? SessionId, JsonNode? UserNode);

    private static async Task<ResolvedCaller> ResolveCallerAsync(
        HttpContext http, Project project, PraxyDb db, AppAuthService appAuth, ApiKeyService apiKeys,
        ConsoleAuthService consoleAuth, TicketStore tickets, IRoleResolver roleResolver, CancellationToken ct)
    {
        var keyToken = http.Request.Headers[AppPrincipalFilter.KeyHeader].FirstOrDefault();
        if (!string.IsNullOrEmpty(keyToken))
        {
            var apiKey = await apiKeys.ResolveAsync(project.Id, keyToken, ct)
                ?? throw PraxyException.Unauthorized("Invalid API key.");
            RequireRealtimeScope(apiKey);
            return await BuildAsync(new RequestPrincipal.Key(apiKey), apiKey.BypassRowPermissions, null, null, null);
        }

        var ticket = http.Request.Query["ticket"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ticket))
            return await ResolveFromTicketAsync(ticket, project, db, roleResolver, tickets, ct);

        var sessionToken = http.Request.Headers[AppPrincipalFilter.SessionHeader].FirstOrDefault()
            ?? http.Request.Cookies[AppSessionCookie.Name(project.Id)];
        if (!string.IsNullOrEmpty(sessionToken))
        {
            var resolved = await appAuth.ResolveSessionAsync(project.Id, sessionToken, ct);
            if (resolved is not null)
                return await BuildAsync(new RequestPrincipal.AppUser(resolved.User, resolved.Session), false,
                    resolved.User.Id, resolved.Session.Id, UserNode(resolved.User));
        }

        var operatorToken = http.Request.Cookies[RequireOperatorFilter.CookieName];
        if (!string.IsNullOrEmpty(operatorToken))
        {
            var op = await consoleAuth.ResolveSessionAsync(operatorToken, ct);
            if (op is { } resolvedOperator)
            {
                // Same "does this operator's org own this project" check as ConsoleProjectFilter —
                // an operator sees every channel on a project they administer, bypassing role
                // matching entirely, same as ConsoleRowEndpoints bypasses row permissions.
                var hasAccess = await (
                    from p in db.Projects
                    join m in db.OrganizationMembers on p.OrganizationId equals m.OrganizationId
                    where m.UserId == resolvedOperator.Account.Id && p.Id == project.Id
                    select 1).AnyAsync(ct);
                if (hasAccess)
                    return new ResolvedCaller(null, [], true, null, null, null);
            }
        }

        return await BuildAsync(new RequestPrincipal.Guest(), false, null, null, null);

        async Task<ResolvedCaller> BuildAsync(RequestPrincipal principal, bool bypass, Guid? userId, Guid? sessionId, JsonNode? userNode)
        {
            if (bypass)
                return new ResolvedCaller(null, [], true, userId, sessionId, userNode);
            var roles = await roleResolver.ResolveAsync(principal, ct);
            return new ResolvedCaller(principal, roles, false, userId, sessionId, userNode);
        }
    }

    private static async Task<ResolvedCaller> ResolveFromTicketAsync(
        string ticket, Project project, PraxyDb db, IRoleResolver roleResolver, TicketStore tickets, CancellationToken ct)
    {
        if (!tickets.TryConsume(ticket, out var data) || data.ProjectId != project.Id)
            throw PraxyException.Unauthorized("Invalid or expired realtime ticket.");

        if (data.ApiKeyId is { } keyId)
        {
            var apiKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.ProjectId == project.Id, ct);
            if (apiKey is not null && (apiKey.ExpiresAt is null || apiKey.ExpiresAt > DateTimeOffset.UtcNow))
            {
                RequireRealtimeScope(apiKey);
                var roles = await roleResolver.ResolveAsync(new RequestPrincipal.Key(apiKey), ct);
                return new ResolvedCaller(new RequestPrincipal.Key(apiKey), roles, apiKey.BypassRowPermissions, null, null, null);
            }
        }
        else if (data.SessionId is { } sessionId)
        {
            var pair = await (
                from s in db.Sessions
                join u in db.Users on s.UserId equals u.Id
                where s.Id == sessionId
                select new { Session = s, User = u }).FirstOrDefaultAsync(ct);
            if (pair is not null && pair.Session.ExpiresAt > DateTimeOffset.UtcNow && pair.User.Status && pair.User.ProjectId == project.Id)
            {
                var principal = new RequestPrincipal.AppUser(pair.User, pair.Session);
                var roles = await roleResolver.ResolveAsync(principal, ct);
                return new ResolvedCaller(principal, roles, false, pair.User.Id, pair.Session.Id, UserNode(pair.User));
            }
        }

        // The referenced session/key was revoked between minting and redeeming the ticket — degrade to guest.
        var guestRoles = await roleResolver.ResolveAsync(new RequestPrincipal.Guest(), ct);
        return new ResolvedCaller(new RequestPrincipal.Guest(), guestRoles, false, null, null, null);
    }

    private static void RequireRealtimeScope(ApiKey apiKey)
    {
        if (!apiKey.Scopes.Contains(ApiKeyScopes.DatabasesRead))
            throw new PraxyException(401, ErrorTypes.GeneralUnauthorizedScope,
                $"The API key is missing the '{ApiKeyScopes.DatabasesRead}' scope.");
    }

    private static JsonNode UserNode(User user) =>
        new JsonObject { ["$id"] = Ids.Wire(user.Id), ["email"] = user.Email, ["name"] = user.Name };
}

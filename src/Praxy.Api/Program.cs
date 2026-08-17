using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praxy.Api.Endpoints;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Auth.OAuth;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Functions;
using Praxy.Messaging;
using Praxy.Persistence;
using Praxy.Realtime;
using Praxy.Tables;
using Praxy.Tables.Quotas;
using Praxy.Webhooks;
using Scalar.AspNetCore;
using Serilog;

// Two-stage Serilog: a bootstrap logger catches startup failures until the real one is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    var connectionString = builder.Configuration.GetConnectionString("praxy")
        ?? throw new InvalidOperationException("ConnectionStrings:praxy is not configured.");

    // architecture.md §11's threat model claims "statement_timeout on every connection" as a
    // resource-exhaustion mitigation — true for DDL/schema-job connections (each sets its own via
    // SET LOCAL) but not, until Phase 9's security pass caught it, for the shared pool the data
    // plane's row reads/writes run on. The Options startup parameter applies a default to every
    // connection opened from this pool; DDL paths that need longer already SET a session-level
    // override afterward, which takes precedence for the rest of that connection's lifetime.
    var statementTimeoutMs = builder.Configuration.GetValue("Praxy:Database:StatementTimeoutSeconds", 30) * 1000;
    var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    connectionStringBuilder.Options = string.IsNullOrEmpty(connectionStringBuilder.Options)
        ? $"-c statement_timeout={statementTimeoutMs}"
        : $"{connectionStringBuilder.Options} -c statement_timeout={statementTimeoutMs}";

    builder.Services.AddNpgsqlDataSource(connectionStringBuilder.ConnectionString);
    builder.Services.AddDbContext<PraxyDb>((sp, o) => o
        .UseNpgsql(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            npgsql => npgsql.MigrationsHistoryTable(PraxyDb.MigrationsHistoryTable, PraxyDb.Schema))
        .UseSnakeCaseNamingConvention());

    builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
    builder.Services.AddSingleton(new Argon2Options());
    builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
    builder.Services.AddSingleton<SetupTokenService>();
    builder.Services.AddScoped<ConsoleAuthService>();

    // ---- Phase 1: app-user auth ----
    builder.Services.AddSingleton(new InstanceKey(
        builder.Configuration["PRAXY_SECRET_KEY"] ?? builder.Configuration["Praxy:SecretKey"]));
    builder.Services.AddSingleton<ISessionCache>(sp => new InMemorySessionCache(
        sp.GetRequiredService<IEventBus>(),
        TimeSpan.FromSeconds(builder.Configuration.GetValue("Praxy:Auth:SessionCacheSeconds", 60))));
    builder.Services.AddScoped<AppAuthService>();
    builder.Services.AddScoped<TeamsService>();
    builder.Services.AddScoped<OAuthService>();
    builder.Services.AddScoped<ApiKeyService>();
    builder.Services.AddScoped<IRoleResolver, RoleResolver>();
    builder.Services.AddScoped<AccountJwtService>();

    var smtp = new SmtpOptions();
    builder.Configuration.GetSection("Praxy:Smtp").Bind(smtp);
    builder.Services.AddSingleton(smtp);
    if (smtp.Configured)
        builder.Services.AddSingleton<IEmailSender>(new SmtpEmailSender(smtp));
    else
        builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

    builder.Services.AddHttpClient<GoogleOAuthProvider>();
    builder.Services.AddTransient<IOAuthProvider>(sp => sp.GetRequiredService<GoogleOAuthProvider>());
    builder.Services.AddScoped<IOAuthProviderRegistry, OAuthProviderRegistry>();

    // ---- Phase 9: org-level quotas (read by the schema engine below, so bind first) ----
    builder.Services.AddSingleton(new QuotaOptions(
        MaxProjects: builder.Configuration.GetValue("Praxy:Quotas:MaxProjects", 100),
        MaxDatabasesPerProject: builder.Configuration.GetValue("Praxy:Quotas:MaxDatabasesPerProject", 20),
        MaxTablesPerDatabase: builder.Configuration.GetValue("Praxy:Quotas:MaxTablesPerDatabase", 200),
        MaxColumnsPerTable: builder.Configuration.GetValue("Praxy:Quotas:MaxColumnsPerTable", 200),
        MaxIndexesPerTable: builder.Configuration.GetValue("Praxy:Quotas:MaxIndexesPerTable", 64)));
    builder.Services.AddScoped<QuotaService>();

    // ---- Phase 2: schema engine ----
    builder.Services.AddSingleton<CatalogCache>();
    builder.Services.AddScoped<DatabasesService>();
    builder.Services.AddScoped<TablesService>();
    builder.Services.AddScoped<ColumnsService>();
    builder.Services.AddScoped<IndexesService>();
    builder.Services.AddScoped<SchemaJobsService>();
    builder.Services.AddSingleton<SchemaJobSignal>();
    builder.Services.AddSingleton(new SchemaJobRunnerOptions(
        PollIntervalSeconds: builder.Configuration.GetValue("Praxy:Tables:SchemaJobs:PollIntervalSeconds", 2),
        IndexBuildTimeoutSeconds: builder.Configuration.GetValue("Praxy:Tables:SchemaJobs:IndexBuildTimeoutSeconds", 1800)));
    builder.Services.AddHostedService<SchemaJobRunner>();

    // ---- Phase 3: data plane ----
    builder.Services.AddScoped<RowsService>();

    // ---- Phase 4: realtime ----
    builder.Services.AddSingleton<ConnectionRegistry>();
    builder.Services.AddSingleton<TicketStore>();
    builder.Services.AddSingleton(new RealtimeOptions(
        MaxConnectionsPerProject: builder.Configuration.GetValue("Praxy:Realtime:MaxConnectionsPerProject", 1000)));
    builder.Services.AddHostedService<RealtimeHub>();

    // ---- Phase 6: webhooks ----
    builder.Services.AddScoped<WebhookSubscriptionsService>();
    builder.Services.AddSingleton<WebhookDeliverySignal>();
    builder.Services.AddScoped<WebhookDeliveriesService>();
    var webhookOptions = new WebhookOptions(
        DispatchPollIntervalSeconds: builder.Configuration.GetValue("Praxy:Webhooks:DispatchPollIntervalSeconds", 2),
        DeliveryPollIntervalSeconds: builder.Configuration.GetValue("Praxy:Webhooks:DeliveryPollIntervalSeconds", 2),
        TimeoutSeconds: builder.Configuration.GetValue("Praxy:Webhooks:TimeoutSeconds", 15),
        MaxAttempts: builder.Configuration.GetValue("Praxy:Webhooks:MaxAttempts", 10),
        BackoffBaseSeconds: builder.Configuration.GetValue("Praxy:Webhooks:BackoffBaseSeconds", 1.0),
        BackoffCapSeconds: builder.Configuration.GetValue("Praxy:Webhooks:BackoffCapSeconds", 300.0),
        DisableAfterConsecutiveFailures: builder.Configuration.GetValue("Praxy:Webhooks:DisableAfterConsecutiveFailures", 10),
        AllowPrivateNetworkTargets: builder.Configuration.GetValue("Praxy:Webhooks:AllowPrivateNetworkTargets", false),
        MaxResponseBodyCaptureBytes: builder.Configuration.GetValue("Praxy:Webhooks:MaxResponseBodyCaptureBytes", 8192));
    builder.Services.AddSingleton(webhookOptions);
    // SSRF guard lives at connect time (SsrfGuard.ConnectAsync resolves DNS itself and connects to
    // the resolved address directly), and AllowAutoRedirect=false is the whole of "no redirects
    // followed cross-origin" — simpler and safer than allowing same-origin redirects selectively.
    builder.Services.AddHttpClient<WebhookHttpClient>(c => c.Timeout = TimeSpan.FromSeconds(webhookOptions.TimeoutSeconds))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) => await SsrfGuard.ConnectAsync(
                context.DnsEndPoint.Host, context.DnsEndPoint.Port, webhookOptions.AllowPrivateNetworkTargets, ct),
        });
    builder.Services.AddHostedService<WebhookOutboxDispatcher>();
    builder.Services.AddHostedService<WebhookDeliveryWorker>();

    // ---- Phase 7: functions ----
    var functionsOptions = new FunctionsOptions(
        DockerEndpoint: builder.Configuration["Praxy:Functions:DockerEndpoint"]
            ?? Environment.GetEnvironmentVariable("DOCKER_HOST") ?? "unix:///var/run/docker.sock",
        DartBaseImage: builder.Configuration["Praxy:Functions:DartBaseImage"] ?? "dart:stable",
        NodeBaseImage: builder.Configuration["Praxy:Functions:NodeBaseImage"] ?? "node:22-alpine",
        BuildPollIntervalSeconds: builder.Configuration.GetValue("Praxy:Functions:BuildPollIntervalSeconds", 2),
        ExecutionPollIntervalSeconds: builder.Configuration.GetValue("Praxy:Functions:ExecutionPollIntervalSeconds", 2),
        SchedulePollIntervalSeconds: builder.Configuration.GetValue("Praxy:Functions:SchedulePollIntervalSeconds", 5),
        BuildTimeoutSeconds: builder.Configuration.GetValue("Praxy:Functions:BuildTimeoutSeconds", 600),
        ColdStartTimeoutSeconds: builder.Configuration.GetValue("Praxy:Functions:ColdStartTimeoutSeconds", 60),
        MaxSyncTimeoutSeconds: builder.Configuration.GetValue("Praxy:Functions:MaxSyncTimeoutSeconds", 30),
        WarmPoolSize: builder.Configuration.GetValue("Praxy:Functions:WarmPoolSize", 10),
        MaxIdleSeconds: builder.Configuration.GetValue("Praxy:Functions:MaxIdleSeconds", 300),
        PoolSweepIntervalSeconds: builder.Configuration.GetValue("Praxy:Functions:PoolSweepIntervalSeconds", 30),
        MemoryLimitMb: builder.Configuration.GetValue("Praxy:Functions:MemoryLimitMb", 256L),
        CpuLimit: builder.Configuration.GetValue("Praxy:Functions:CpuLimit", 1.0),
        MaxResponseCaptureBytes: builder.Configuration.GetValue("Praxy:Functions:MaxResponseCaptureBytes", 65536),
        MaxSourceBytes: builder.Configuration.GetValue("Praxy:Functions:MaxSourceBytes", 26_214_400L));
    builder.Services.AddSingleton(functionsOptions);
    builder.Services.AddSingleton<DockerExecutor>();
    builder.Services.AddSingleton<WarmPool>();
    builder.Services.AddSingleton<FunctionBuildSignal>();
    builder.Services.AddSingleton<FunctionExecutionSignal>();
    builder.Services.AddScoped<FunctionsService>();
    builder.Services.AddScoped<FunctionExecutionService>();
    builder.Services.AddHostedService<FunctionBuildWorker>();
    builder.Services.AddHostedService<FunctionExecutionWorker>();
    builder.Services.AddHostedService<FunctionEventDispatcher>();
    builder.Services.AddHostedService<FunctionScheduler>();
    builder.Services.AddHostedService<FunctionPoolSweeper>();

    // ---- Phase 8: messaging ----
    var messagingOptions = new MessagingOptions(
        SendPollIntervalSeconds: builder.Configuration.GetValue("Praxy:Messaging:SendPollIntervalSeconds", 2),
        MaxSubjectLength: builder.Configuration.GetValue("Praxy:Messaging:MaxSubjectLength", 998),
        MaxBodyLength: builder.Configuration.GetValue("Praxy:Messaging:MaxBodyLength", 65536),
        MaxTargetsPerMessage: builder.Configuration.GetValue("Praxy:Messaging:MaxTargetsPerMessage", 10_000));
    builder.Services.AddSingleton(messagingOptions);
    builder.Services.AddScoped<MessagingProvidersService>();
    builder.Services.AddScoped<EmailProviderResolver>();
    builder.Services.AddScoped<MessagingTargetsService>();
    builder.Services.AddScoped<MessagingTopicsService>();
    builder.Services.AddScoped<MessagingTemplatesService>();
    builder.Services.AddScoped<IAuthEmailSender, AuthEmailBridge>();
    builder.Services.AddSingleton<MessageSendSignal>();
    builder.Services.AddScoped<MessagesService>();
    builder.Services.AddHostedService<MessageSendWorker>();

    // Tight buckets on auth endpoints, partitioned on project (or key) before IP — a spoofable
    // source address alone never carves out someone else's budget. Limits are configurable and
    // loud when tripped: 429 (NOT the 503 default), Retry-After, RateLimit-*.
    var rateLimits = new Dictionary<string, (int PermitLimit, int WindowSeconds)>
    {
        ["auth"] = (
            builder.Configuration.GetValue("Praxy:RateLimits:Auth:PermitLimit", 10),
            builder.Configuration.GetValue("Praxy:RateLimits:Auth:WindowSeconds", 60)),
        ["auth-email"] = (
            builder.Configuration.GetValue("Praxy:RateLimits:AuthEmail:PermitLimit", 5),
            builder.Configuration.GetValue("Praxy:RateLimits:AuthEmail:WindowSeconds", 600)),
    };
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (rejected, ct) =>
        {
            var http = rejected.HttpContext;
            var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            var limits = policy is not null && rateLimits.TryGetValue(policy, out var found)
                ? found
                : rateLimits["auth"];
            var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var window)
                ? Math.Max(1, (int)Math.Ceiling(window.TotalSeconds))
                : limits.WindowSeconds;

            http.Response.Headers.RetryAfter = retryAfter.ToString();
            http.Response.Headers["RateLimit-Limit"] = limits.PermitLimit.ToString();
            http.Response.Headers["RateLimit-Remaining"] = "0";
            http.Response.Headers["RateLimit-Reset"] = retryAfter.ToString();
            await http.Response.WriteAsJsonAsync(ErrorEnvelope.Create(
                http, StatusCodes.Status429TooManyRequests, ErrorTypes.GeneralRateLimitExceeded,
                "Rate limit exceeded. Try again later."), ct);
        };

        static string PartitionKey(HttpContext http) =>
            $"{http.Request.Headers[DataPlaneEndpoints.ProjectHeader].FirstOrDefault() ?? http.Request.Query["project"].FirstOrDefault() ?? "-"}" +
            $"|{http.Connection.RemoteIpAddress?.ToString() ?? "-"}";

        foreach (var (name, limits) in rateLimits)
            options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                    QueueLimit = 0,
                }));
    });

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Catalog migrations run before the server accepts traffic, serialized cluster-wide
    // by a session-level advisory lock.
    await CatalogMigrator.MigrateAsync(app.Services);

    // The setup token is only relevant while unclaimed; announce it in the logs at startup.
    using (var scope = app.Services.CreateScope())
    {
        var auth = scope.ServiceProvider.GetRequiredService<ConsoleAuthService>();
        if (!await auth.IsClaimedAsync())
            app.Services.GetRequiredService<SetupTokenService>().GenerateAndAnnounce();
    }

    var instanceKey = app.Services.GetRequiredService<InstanceKey>();
    if (instanceKey.Ephemeral)
        Log.Warning(
            "PRAXY_SECRET_KEY is not set — using an ephemeral instance key. OAuth logins in " +
            "flight and encrypted provider tokens will not survive a restart. Set it in .env for production.");

    // Opt-in only (Praxy:TrustForwardedHeaders, default false) and first in the pipeline: everything
    // downstream (the session cookie's Secure flag, rate-limit partitioning, audit/session IP
    // logging) reads Request.IsHttps/RemoteIpAddress, which are wrong behind a reverse proxy unless
    // corrected here. Trusting every proxy (KnownNetworks/KnownProxies cleared) is only safe because
    // this is meant for exactly one trusted proxy reached over the compose network, never a
    // public-facing api port — enabling this while the api port is also directly internet-reachable
    // lets any direct-connecting client spoof its own scheme/IP.
    if (builder.Configuration.GetValue("Praxy:TrustForwardedHeaders", false))
    {
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeadersOptions);
    }

    app.UseMiddleware<RequestIdMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    // After the error middleware so an unknown origin gets the public 403 envelope.
    app.UseMiddleware<PlatformCorsMiddleware>();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Explicit and *after* static files: otherwise WebApplication auto-prepends routing,
    // the /console/{*path} fallback endpoint matches first, and the static middleware
    // (which yields to matched endpoints) never serves the console assets.
    app.UseRouting();

    // Must follow UseRouting so per-endpoint policies resolve.
    app.UseRateLimiter();

    // dotnet-stack.md's verified WebSocket shape: native ping/pong dead-peer detection via
    // KeepAliveInterval/KeepAliveTimeout, configured per-accept in RealtimeEndpoints.
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    if (app.Environment.IsDevelopment())
    {
        // Dev-only: the OpenAPI document and Scalar UI disclose the full API surface.
        app.MapOpenApi();
        app.MapScalarApiReference(o => o.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
    }

    app.MapGet("/v1/health", () => Results.Ok(new { status = "ok", version = Praxy.Core.PraxyVersion.Current }));

    CapabilitiesEndpoints.Map(app);
    ConsoleAuthEndpoints.Map(app);
    ProjectEndpoints.Map(app);
    DataPlaneEndpoints.Map(app);
    AccountEndpoints.Map(app);
    TeamEndpoints.Map(app);
    UsersServerEndpoints.Map(app);
    ConsoleAuthAdminEndpoints.Map(app);
    DatabaseEndpoints.Map(app);
    ConsoleDatabaseEndpoints.Map(app);
    RowEndpoints.Map(app);
    ConsoleRowEndpoints.Map(app);
    RealtimeEndpoints.Map(app);
    WebhookEndpoints.Map(app);
    FunctionEndpoints.Map(app);
    MessagingEndpoints.Map(app);

    app.MapGet("/", () => Results.Redirect("/console"));
    // SPA fallback: any /console/* route serves the app shell; client routing takes over.
    app.MapFallbackToFile("/console/{*path}", "console/index.html");
    // Anything else unmatched gets the public 404 envelope rather than a bare status code.
    app.MapFallback(() => { throw new PraxyException(404, ErrorTypes.GeneralRouteNotFound, "Route not found."); });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Praxy API terminated unexpectedly at startup");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Exposes the entry point to WebApplicationFactory-based integration tests.
public partial class Program;

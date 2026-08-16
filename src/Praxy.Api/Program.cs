using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Praxy.Api.Endpoints;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Auth.OAuth;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
using Praxy.Realtime;
using Praxy.Tables;
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

    builder.Services.AddNpgsqlDataSource(connectionString);
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

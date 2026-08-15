using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Praxy.Api.Endpoints;
using Praxy.Api.Infrastructure;
using Praxy.Auth;
using Praxy.Core.Errors;
using Praxy.Events;
using Praxy.Persistence;
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

    app.UseMiddleware<RequestIdMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();

    app.UseDefaultFiles();
    app.UseStaticFiles();

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

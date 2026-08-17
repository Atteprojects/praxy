using Praxy.LoadTests;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var options = ParseOptions(args[1..]);
var endpoint = options.GetValueOrDefault("endpoint", "http://localhost:5090");
var email = options.GetValueOrDefault("email", "loadtest@praxy.test");
var password = options.GetValueOrDefault("password", "load-test-password-123");

switch (args[0])
{
    case "schemas":
    {
        var connection = options.GetValueOrDefault("connection")
            ?? throw new ArgumentException("schemas needs --connection \"Host=...;Port=...;Database=...;Username=...;Password=...\" (direct Postgres access, used to raise the org's database quota and measure pg_catalog scan time).");
        await SchemaLoadTest.RunAsync(
            endpoint, connection,
            count: int.Parse(options.GetValueOrDefault("count", "1000")),
            concurrency: int.Parse(options.GetValueOrDefault("concurrency", "20")),
            email, password);
        return 0;
    }
    case "websockets":
    {
        await WebSocketLoadTest.RunAsync(
            endpoint,
            projects: int.Parse(options.GetValueOrDefault("projects", "10")),
            connectionsPerProject: int.Parse(options.GetValueOrDefault("connections-per-project", "1000")),
            rampConcurrency: int.Parse(options.GetValueOrDefault("ramp-concurrency", "200")),
            email, password);
        return 0;
    }
    case "fuzz":
    {
        await QueryFuzzTest.RunAsync(
            endpoint,
            iterations: int.Parse(options.GetValueOrDefault("iterations", "5000")),
            concurrency: int.Parse(options.GetValueOrDefault("concurrency", "20")),
            email, password);
        return 0;
    }
    default:
        PrintUsage();
        return 1;
}

static Dictionary<string, string> ParseOptions(string[] rest)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = rest[i][2..];
        var value = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal) ? rest[++i] : "true";
        result[key] = value;
    }
    return result;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Praxy load test tools (roadmap Phase 9). Run against a live instance — dev, self-host, or a
        disposable throwaway one; every command claims the instance if unclaimed (or logs in if
        already claimed) and creates its own scratch projects, so it's safe to run repeatedly.

        Usage:
          dotnet run -- schemas    --connection "<npgsql connection string>" [--endpoint http://localhost:5090] [--count 1000] [--concurrency 20]
          dotnet run -- websockets [--endpoint http://localhost:5090] [--projects 10] [--connections-per-project 1000] [--ramp-concurrency 200]
          dotnet run -- fuzz       [--endpoint http://localhost:5090] [--iterations 5000] [--concurrency 20]

        Common: [--email loadtest@praxy.test] [--password load-test-password-123]

        `websockets` opens real OS sockets — raise the client shell's file-descriptor limit first
        (`ulimit -n 20000`) or connection counts will fail well short of the target.
        """);
}

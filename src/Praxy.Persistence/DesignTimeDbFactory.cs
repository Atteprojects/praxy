using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Praxy.Persistence;

/// <summary>Used only by <c>dotnet ef migrations</c>; never at runtime.</summary>
public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<PraxyDb>
{
    public PraxyDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PraxyDb>()
            .UseNpgsql(
                "Host=localhost;Database=praxy;Username=praxy;Password=design-time-only",
                o => o.MigrationsHistoryTable(PraxyDb.MigrationsHistoryTable, PraxyDb.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PraxyDb(options);
    }
}

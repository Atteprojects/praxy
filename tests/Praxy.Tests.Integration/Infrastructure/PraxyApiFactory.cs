using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Praxy.Tests.Integration.Infrastructure;

public sealed class PraxyApiFactory(string connectionString, IDictionary<string, string?>? extraSettings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:praxy", connectionString);
        if (extraSettings is not null)
            foreach (var (key, value) in extraSettings)
                builder.UseSetting(key, value);
    }
}

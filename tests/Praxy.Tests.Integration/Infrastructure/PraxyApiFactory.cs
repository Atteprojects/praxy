using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Praxy.Tests.Integration.Infrastructure;

public sealed class PraxyApiFactory(
    string connectionString,
    IDictionary<string, string?>? extraSettings = null,
    Action<IServiceCollection>? testServices = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:praxy", connectionString);
        if (extraSettings is not null)
            foreach (var (key, value) in extraSettings)
                builder.UseSetting(key, value);
        if (testServices is not null)
            builder.ConfigureTestServices(testServices);
    }
}

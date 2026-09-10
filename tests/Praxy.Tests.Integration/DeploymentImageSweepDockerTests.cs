using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Praxy.Core;
using Praxy.Sites;
using Praxy.Tests.Integration.Infrastructure;

namespace Praxy.Tests.Integration;

/// <summary>
/// The Docker half of image reclamation, against a real daemon:
/// <c>SiteDockerExecutor.ListDeploymentImageTagsAsync</c> must actually find this product's images,
/// and <c>RemoveImageAsync</c> must actually remove one.
///
/// <para><b>Why this is worth a Docker-dependent test.</b> The listing is a server-side
/// <c>reference</c> filter. If that filter matched nothing — a wrong filter key, a pattern Docker
/// doesn't accept — the sweep would find no images, reclaim nothing, log nothing, and pass every
/// policy unit test in the suite. A sweeper that silently does nothing is indistinguishable from a
/// working one until a disk fills up months later, which is exactly how the leak this fixes went
/// unnoticed for two weeks of deploys.</para>
///
/// <para>No build here: an image is an image to the daemon, so this re-tags the one Testcontainers
/// has already pulled rather than spending a real Next.js build to produce bytes that are never
/// run.</para>
/// </summary>
public class DeploymentImageSweepDockerTests(PostgresContainerFixture pg) : AuthTestBase(pg)
{
    /// <summary>Already present — the Postgres fixture pulls it before any test runs.</summary>
    private const string SourceImage = "postgis/postgis:17-3.6-alpine";

    [Fact]
    public async Task Finds_and_removes_this_products_deployment_images_and_leaves_everything_else_alone()
    {
        var docker = Factory.Services.GetRequiredService<SiteDockerExecutor>();
        using var client = new DockerClientBuilder().Build();

        var deploymentId = Guid.CreateVersion7();
        var tag = $"{SiteDockerExecutor.ImageTagPrefix}{Ids.Wire(deploymentId)}";
        await client.Images.TagImageAsync(
            SourceImage, new ImageTagParameters { RepositoryName = tag, Tag = "latest" }, default);

        try
        {
            var listed = await docker.ListDeploymentImageTagsAsync(default);

            Assert.Contains($"{tag}:latest", listed);
            // The filter selects our images and only ours — the source image is right there on the
            // same daemon under a different name, and must never be a candidate for reclamation.
            Assert.DoesNotContain(SourceImage, listed);
            Assert.All(listed, t => Assert.StartsWith(SiteDockerExecutor.ImageTagPrefix, t, StringComparison.Ordinal));

            Assert.True(await docker.RemoveImageAsync($"{tag}:latest", default));
            Assert.DoesNotContain($"{tag}:latest", await docker.ListDeploymentImageTagsAsync(default));
        }
        finally
        {
            // Untag rather than delete: this only ever removes the extra name, never the shared
            // image underneath, which the Postgres fixture and every other test still need.
            await docker.RemoveImageAsync($"{tag}:latest", default);
        }
    }

    [Fact]
    public async Task Removing_an_image_that_is_already_gone_reports_false_rather_than_throwing()
    {
        // The sweep runs on a timer against a daemon it doesn't own, so "already gone" is ordinary,
        // not exceptional — and the count it logs as "reclaimed" has to mean reclaimed.
        var docker = Factory.Services.GetRequiredService<SiteDockerExecutor>();

        var missing = $"{SiteDockerExecutor.ImageTagPrefix}{Ids.Wire(Guid.CreateVersion7())}:latest";

        Assert.False(await docker.RemoveImageAsync(missing, default));
    }
}

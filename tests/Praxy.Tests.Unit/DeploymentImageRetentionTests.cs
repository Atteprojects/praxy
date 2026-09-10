using Praxy.Api.Infrastructure;
using Praxy.Core;
using Praxy.Sites;
using Row = Praxy.Api.Infrastructure.DeploymentImageSweeper.DeploymentRow;

namespace Praxy.Tests.Unit;

/// <summary>
/// The retention policy itself, exercised without a Docker daemon. This is where a mistake deletes
/// a rollback target that an operator only discovers they needed during a bad deploy, so every rule
/// gets a test, and — more importantly — so does every case that must NOT be reclaimed.
/// </summary>
public class DeploymentImageRetentionTests
{
    private const string Prefix = SiteDockerExecutor.ImageTagPrefix;

    private static string Tag(Guid id) => $"{Prefix}{Ids.Wire(id)}:latest";

    private static Row Ready(Guid id, Guid parent, int ageDays) =>
        new(id, parent, "ready", DateTimeOffset.UtcNow.AddDays(-ageDays));

    [Fact]
    public void Reclaims_an_image_whose_deployment_row_is_gone()
    {
        // The deleted-site case: SitesService.DeleteAsync removes the rows and never touched the
        // image, so every image that site ever built was stranded with nothing referencing it.
        var orphan = Guid.CreateVersion7();

        var expendable = DeploymentImageSweeper.Expendable(
            [Tag(orphan)], Prefix, [], [], keep: 5);

        Assert.Equal([(Tag(orphan), orphan)], expendable);
    }

    [Fact]
    public void Keeps_a_deployment_that_has_not_finished_building()
    {
        // The reason the orphan rule needs no age guard: a row exists from "queued" onward, so an
        // in-flight build is never mistaken for an orphan. If this ever fails, the sweeper can
        // delete an image out from under a build that is about to record it.
        var building = Guid.CreateVersion7();
        var rows = new[] { new Row(building, Guid.CreateVersion7(), "building", DateTimeOffset.UtcNow) };

        var expendable = DeploymentImageSweeper.Expendable(
            [Tag(building)], Prefix, rows, [], keep: 5);

        Assert.Empty(expendable);
    }

    [Fact]
    public void Keeps_the_most_recent_builds_and_reclaims_what_falls_out_the_back()
    {
        var site = Guid.CreateVersion7();
        var rows = Enumerable.Range(0, 5).Select(i => Ready(Guid.CreateVersion7(), site, ageDays: i)).ToList();

        var expendable = DeploymentImageSweeper.Expendable(
            rows.Select(r => Tag(r.Id)).ToList(), Prefix, rows, [], keep: 3);

        // Newest three survive; the two oldest go.
        Assert.Equal(
            [rows[3].Id, rows[4].Id],
            expendable.Select(e => e.DeploymentId).OrderBy(id => rows.FindIndex(r => r.Id == id)));
    }

    [Fact]
    public void Never_reclaims_the_active_deployment_however_old_it_is()
    {
        // Roll back to an old build and stay there: the active deployment is now older than every
        // deployment in the keep-window, and reclaiming it would delete the image of the site that
        // is serving production right now.
        var site = Guid.CreateVersion7();
        var old = Ready(Guid.CreateVersion7(), site, ageDays: 90);
        var rows = new List<Row> { old };
        rows.AddRange(Enumerable.Range(0, 3).Select(i => Ready(Guid.CreateVersion7(), site, ageDays: i)));

        var expendable = DeploymentImageSweeper.Expendable(
            rows.Select(r => Tag(r.Id)).ToList(), Prefix, rows, [old.Id], keep: 2);

        Assert.DoesNotContain(old.Id, expendable.Select(e => e.DeploymentId));
    }

    [Fact]
    public void Window_is_per_resource_so_one_busy_site_cannot_evict_anothers_only_build()
    {
        // A global "keep the N newest" list would let a site deploying ten times a day push every
        // other site's only rollback target off the end.
        var busy = Guid.CreateVersion7();
        var quiet = Guid.CreateVersion7();
        var quietOnly = Ready(Guid.CreateVersion7(), quiet, ageDays: 30);
        var rows = new List<Row> { quietOnly };
        rows.AddRange(Enumerable.Range(0, 10).Select(i => Ready(Guid.CreateVersion7(), busy, ageDays: i)));

        var expendable = DeploymentImageSweeper.Expendable(
            rows.Select(r => Tag(r.Id)).ToList(), Prefix, rows, [], keep: 3);

        Assert.DoesNotContain(quietOnly.Id, expendable.Select(e => e.DeploymentId));
        Assert.Equal(7, expendable.Count); // 10 - 3 kept, from the busy site alone.
    }

    [Fact]
    public void Ignores_a_tag_that_is_not_one_of_ours()
    {
        // The prefix filter is the only thing between this policy and an unrelated image, so it
        // fails closed: anything that doesn't parse as our tag is left entirely alone.
        var expendable = DeploymentImageSweeper.Expendable(
            ["postgres:17-alpine", $"{Prefix}not-a-guid:latest", "praxy-api:latest"],
            Prefix, [], [], keep: 5);

        Assert.Empty(expendable);
    }

    [Fact]
    public void Reads_the_deployment_id_back_out_of_a_tag_with_no_explicit_version()
    {
        // Docker reports RepoTags with the tag attached, but an image referenced bare must parse
        // identically — the id, not the ":latest", is what identifies the deployment.
        var id = Guid.CreateVersion7();

        var expendable = DeploymentImageSweeper.Expendable(
            [$"{Prefix}{Ids.Wire(id)}"], Prefix, [], [], keep: 5);

        Assert.Equal([($"{Prefix}{Ids.Wire(id)}", id)], expendable);
    }

    [Fact]
    public void Keep_of_zero_still_spares_the_active_deployment()
    {
        // An operator setting this to 0 is asking for "no rollback history", not "take production
        // down at the next sweep".
        var site = Guid.CreateVersion7();
        var active = Ready(Guid.CreateVersion7(), site, ageDays: 0);
        var superseded = Ready(Guid.CreateVersion7(), site, ageDays: 1);
        var rows = new List<Row> { active, superseded };

        var expendable = DeploymentImageSweeper.Expendable(
            rows.Select(r => Tag(r.Id)).ToList(), Prefix, rows, [active.Id], keep: 0);

        Assert.Equal([superseded.Id], expendable.Select(e => e.DeploymentId));
    }

    [Fact]
    public void A_failed_build_leaves_nothing_to_reclaim_and_is_not_treated_as_an_orphan()
    {
        // A failed build never produced an image, so its id should never appear in the tag list at
        // all — but if it somehow does, the row exists and is not "ready", so it is left alone
        // rather than reclaimed on the orphan rule.
        var failed = Guid.CreateVersion7();
        var rows = new[] { new Row(failed, Guid.CreateVersion7(), "failed", DateTimeOffset.UtcNow.AddDays(-30)) };

        var expendable = DeploymentImageSweeper.Expendable([Tag(failed)], Prefix, rows, [], keep: 5);

        Assert.Empty(expendable);
    }
}

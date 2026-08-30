using Praxy.Vcs;

namespace Praxy.Tests.Integration;

/// <summary>
/// <see cref="GitCliRepositoryCloner"/> itself, not a fake — <c>SiteGitDeploymentTests</c> and
/// <c>FunctionGitDeploymentTests</c> both swap in <c>IGitRepositoryCloner</c>'s fake precisely because
/// the real one needs a real GitHub App/installation token, so nothing else ever exercises the actual
/// <c>git</c> subprocess invocation. That gap is exactly how a real bug shipped unnoticed: found live
/// 2026-08-30 during the first real owner-test against a real GitHub repository — every clone failed
/// with "Only one of Arguments or ArgumentList may be used." (a .NET <c>Process</c> API misuse, not a
/// git or network problem) — see the fix in <c>GitCliRepositoryCloner.RunGitAsync</c>'s own comment.
/// Needs real outbound network access to reach github.com, same requirement <c>FunctionTests</c>/
/// <c>SiteTests</c> already carry for their own package-registry pulls — not asserting a successful
/// clone (that needs a real installation token this suite doesn't have), just that starting the git
/// subprocess itself doesn't throw before git ever gets a chance to report its own, real error.
/// </summary>
public class GitCliRepositoryClonerTests
{
    [Fact]
    public async Task CloneAsync_starts_the_git_subprocess_without_the_dotnet_argumentlist_conflict()
    {
        var options = new VcsOptions(
            new GitHubAppOptions("app-id", "client-id", "client-secret", "unused", "webhook-secret"),
            CloneTimeoutSeconds: 20);
        var cloner = new GitCliRepositoryCloner(options);

        var ex = await Record.ExceptionAsync(() => cloner.CloneAsync(
            "praxy-test-org/this-repository-does-not-exist-3f9a2b",
            "0000000000000000000000000000000000000",
            "fake-installation-token",
            CancellationToken.None));

        // A real git error (auth failure, "repository not found", DNS) is expected and fine — that's
        // git actually running. The one thing this test exists to rule out is the .NET-level exception
        // that fires before git ever starts, which every prior "clone" attempt hit identically
        // regardless of repository, token, or network state.
        Assert.NotNull(ex);
        Assert.DoesNotContain("Only one of Arguments or ArgumentList", ex.Message, StringComparison.Ordinal);
    }
}

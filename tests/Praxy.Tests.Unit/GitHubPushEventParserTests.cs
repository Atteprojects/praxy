using System.Text;
using Praxy.Vcs;

namespace Praxy.Tests.Unit;

public class GitHubPushEventParserTests
{
    private const string RealisticPushPayload = """
        {
          "ref": "refs/heads/main",
          "before": "0000000000000000000000000000000000000000",
          "after": "abc123def456abc123def456abc123def456abc1",
          "repository": { "id": 1, "full_name": "acme/website" },
          "installation": { "id": 42 },
          "head_commit": { "id": "abc123def456abc123def456abc123def456abc1", "message": "Fix the header" }
        }
        """;

    [Fact]
    public void Parse_extracts_repository_ref_branch_commit_and_installation()
    {
        var evt = GitHubPushEventParser.Parse(Encoding.UTF8.GetBytes(RealisticPushPayload));
        Assert.Equal("acme/website", evt.RepositoryFullName);
        Assert.Equal("refs/heads/main", evt.Ref);
        Assert.Equal("main", evt.Branch);
        Assert.Equal("abc123def456abc123def456abc123def456abc1", evt.CommitSha);
        Assert.Equal("Fix the header", evt.CommitMessage);
        Assert.Equal(42, evt.InstallationId);
    }

    [Fact]
    public void Parse_derives_the_branch_name_from_a_multi_segment_ref()
    {
        const string payload = """{"ref":"refs/heads/feature/nested-branch","after":"a","repository":{"full_name":"o/r"}}""";
        var evt = GitHubPushEventParser.Parse(Encoding.UTF8.GetBytes(payload));
        Assert.Equal("feature/nested-branch", evt.Branch);
    }

    [Fact]
    public void Parse_tolerates_a_missing_head_commit_and_installation()
    {
        const string payload = """{"ref":"refs/heads/main","after":"a","repository":{"full_name":"o/r"}}""";
        var evt = GitHubPushEventParser.Parse(Encoding.UTF8.GetBytes(payload));
        Assert.Equal("", evt.CommitMessage);
        Assert.Null(evt.InstallationId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"ref":"refs/heads/main"}""")]
    [InlineData("""{"repository":{"full_name":"o/r"}}""")]
    [InlineData("""{"ref":"refs/heads/main","repository":{"full_name":"o/r"}}""")]
    public void Parse_throws_for_malformed_or_incomplete_payloads(string payload)
    {
        Assert.Throws<GitHubPushPayloadException>(() => GitHubPushEventParser.Parse(Encoding.UTF8.GetBytes(payload)));
    }
}

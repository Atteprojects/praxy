using System.Text.Json;
using System.Text.RegularExpressions;

namespace Praxy.Vcs;

/// <summary>
/// A parsed GitHub <c>push</c> webhook event — entirely in terms of GitHub's own concepts (a
/// repository, a ref, a commit). Zero Praxy domain types: <c>Praxy.Vcs</c> hands this back to its
/// caller and has no opinion on what, if anything, should happen next — see the type's own remarks in
/// docs/handoff/sites-phase-4-prompt.md for why that boundary matters.
/// </summary>
public sealed record GitHubPushEvent(
    string RepositoryFullName, string Ref, string Branch, string CommitSha, string CommitMessage, long? InstallationId);

public sealed class GitHubPushPayloadException(string message) : Exception(message);

public static partial class GitHubPushEventParser
{
    private const string BranchRefPrefix = "refs/heads/";

    // security-review-phase-1: GitCliRepositoryCloner passes this straight to `git fetch origin
    // <commitSha>` via ArgumentList (no shell, so no shell injection) — but a value starting with
    // `-` would still be parsed by git as an *option* to fetch, not a ref (option injection). A
    // real GitHub push payload's "after" field is always the actual resulting commit hash, which
    // can't start with `-`, and this endpoint only accepts requests signed with the instance's own
    // webhook secret (GitHubWebhookSignature.Verify) — so this is defense-in-depth against a
    // forged or malformed payload, not a fix for a demonstrated exploit path.
    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitShaPattern();

    /// <summary>Throws <see cref="GitHubPushPayloadException"/> for anything not shaped like a real GitHub push payload.</summary>
    public static GitHubPushEvent Parse(ReadOnlySpan<byte> rawBody)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody.ToArray());
        }
        catch (JsonException ex)
        {
            throw new GitHubPushPayloadException($"Push payload is not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var repositoryFullName = root.TryGetProperty("repository", out var repo)
                && repo.TryGetProperty("full_name", out var fullName) ? fullName.GetString() : null;
            var refValue = root.TryGetProperty("ref", out var refEl) ? refEl.GetString() : null;
            var commitSha = root.TryGetProperty("after", out var afterEl) ? afterEl.GetString() : null;

            if (repositoryFullName is null || refValue is null || commitSha is null)
                throw new GitHubPushPayloadException(
                    "Push payload is missing one of repository.full_name, ref, or after.");
            if (!CommitShaPattern().IsMatch(commitSha))
                throw new GitHubPushPayloadException("Push payload's 'after' is not a valid commit SHA.");

            var commitMessage = root.TryGetProperty("head_commit", out var headCommit)
                && headCommit.ValueKind == JsonValueKind.Object
                && headCommit.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString() ?? "" : "";

            long? installationId = root.TryGetProperty("installation", out var installation)
                && installation.ValueKind == JsonValueKind.Object
                && installation.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)
                ? id : null;

            var branch = refValue.StartsWith(BranchRefPrefix, StringComparison.Ordinal)
                ? refValue[BranchRefPrefix.Length..] : refValue;

            return new GitHubPushEvent(repositoryFullName, refValue, branch, commitSha, commitMessage, installationId);
        }
    }
}

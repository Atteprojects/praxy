using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxy.Core.Errors;

namespace Praxy.Vcs;

/// <summary>
/// Signs the short-lived App-identity JWT GitHub's own docs require for App-level API calls (minting
/// installation tokens, reading installation/app metadata) — RS256, `iss` = App id, ~10 minute expiry.
/// Hand-rolled on the BCL (System.Security.Cryptography.RSA + System.Text.Json) rather than pulling in
/// a JWT library: this only ever signs, never verifies someone else's JWT, and the claim set is fixed
/// and tiny — see docs/research/dotnet-stack.md's Phase 4 section for why that tips the balance away
/// from adding a dependency. Pure and deterministic given a fixed clock, so it's unit-tested directly,
/// no interface needed.
/// </summary>
public static class GitHubAppJwt
{
    public static string Create(GitHubAppOptions app, TimeProvider? clock = null)
    {
        // The instance's own GitHub App is unconfigured until the owner sets it up — the default
        // state for every fresh self-hosted instance, not an edge case. Without this check,
        // RSA.ImportFromPem("") throws a raw ArgumentException that surfaces as an unhandled 500
        // instead of a clean, typed error the console can show ("connect GitHub in Settings").
        if (string.IsNullOrWhiteSpace(app.AppId) || string.IsNullOrWhiteSpace(app.PrivateKey))
            throw new PraxyException(422, ErrorTypes.VcsGithubNotConfigured,
                "This instance's GitHub App isn't configured (Praxy:Vcs:GitHub:AppId/PrivateKey) — see docs/self-host.md's Git integration section.");

        clock ??= TimeProvider.System;
        var now = clock.GetUtcNow();

        // GitHub tolerates a little clock drift but not a JWT minted "in the future" from its own
        // clock's perspective — backdating iat by a minute is GitHub's own documented recommendation.
        var header = Base64UrlEncode("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = app.AppId,
        }));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(VcsOptions.DecodePrivateKey(app.PrivateKey));
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

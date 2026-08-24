namespace Praxy.Sites;

/// <summary>
/// Parses a site's public hostname back into its labels — <c>&lt;key&gt;.&lt;projectId&gt;.{sitesDomain}</c>
/// for a site's production URL, or <c>&lt;deploymentRef&gt;.&lt;key&gt;.&lt;projectId&gt;.{sitesDomain}</c>
/// for a single deployment's preview URL (Sites Phase 2). <paramref name="deploymentRef"/> is the
/// deployment's own wire id (<see cref="Praxy.Core.Ids.Wire"/> — 32 lowercase hex chars, already a
/// valid DNS label, so no separate shortening/collision scheme is needed; it's also exactly what the
/// console already shows for every other resource id). Shared by <see cref="SiteProxyMiddleware"/>
/// (dispatching a proxied request) and the <c>_ask-tls</c> endpoint (deciding whether a hostname
/// Caddy is asking about is even shaped like a site at all, before ever touching the database) — both
/// need the exact same strict parse, since a looser one in either place widens what an attacker can
/// probe.
/// </summary>
public static class SiteHostPattern
{
    public static bool TryParse(string host, string sitesDomain, out string key, out string projectId) =>
        TryParse(host, sitesDomain, out key, out projectId, out _);

    public static bool TryParse(
        string host, string sitesDomain, out string key, out string projectId, out string? deploymentRef)
    {
        key = "";
        projectId = "";
        deploymentRef = null;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(sitesDomain))
            return false;

        var suffix = "." + sitesDomain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = host[..^suffix.Length];
        var labels = prefix.Split('.');

        if (labels.Length == 2 && labels[0].Length > 0 && labels[1].Length > 0)
        {
            key = labels[0];
            projectId = labels[1];
            return true;
        }

        if (labels.Length == 3 && labels[0].Length > 0 && labels[1].Length > 0 && labels[2].Length > 0)
        {
            deploymentRef = labels[0];
            key = labels[1];
            projectId = labels[2];
            return true;
        }

        return false;
    }
}

namespace Praxy.Sites;

/// <summary>
/// Parses <c>&lt;key&gt;.&lt;projectId&gt;.{sitesDomain}</c> back into its two labels. Shared by
/// <see cref="SiteProxyMiddleware"/> (dispatching a proxied request) and the <c>_ask-tls</c> endpoint
/// (deciding whether a hostname Caddy is asking about is even shaped like a site at all, before ever
/// touching the database) — both need the exact same strict parse, since a looser one in either place
/// widens what an attacker can probe.
/// </summary>
public static class SiteHostPattern
{
    public static bool TryParse(string host, string sitesDomain, out string key, out string projectId)
    {
        key = "";
        projectId = "";
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(sitesDomain))
            return false;

        var suffix = "." + sitesDomain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = host[..^suffix.Length];
        var labels = prefix.Split('.');
        if (labels.Length != 2 || labels[0].Length == 0 || labels[1].Length == 0)
            return false;

        key = labels[0];
        projectId = labels[1];
        return true;
    }
}

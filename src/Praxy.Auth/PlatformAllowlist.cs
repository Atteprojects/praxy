namespace Praxy.Auth;

/// <summary>
/// Hostname matching against a project's registered platforms. This single implementation backs
/// both security decisions that depend on it: OAuth/email redirect-URL validation and the CORS
/// origin check. Exact match (case-insensitive) or a leading <c>*.</c> wildcard for subdomains.
/// </summary>
public static class PlatformAllowlist
{
    public static bool HostnameAllowed(IReadOnlyCollection<string> allowedHostnames, string hostname)
    {
        if (hostname.Length == 0)
            return false;
        foreach (var allowed in allowedHostnames)
        {
            if (string.IsNullOrWhiteSpace(allowed))
                continue;
            if (allowed.StartsWith("*.", StringComparison.Ordinal))
            {
                // *.example.com matches a.example.com and a.b.example.com, never example.com itself.
                if (hostname.Length > allowed.Length - 1 &&
                    hostname.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(allowed, hostname, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL whose host is allowlisted.
    /// Everything that rides an email link or an OAuth redirect goes through here.
    /// </summary>
    public static bool RedirectAllowed(IReadOnlyCollection<string> allowedHostnames, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) &&
        HostnameAllowed(allowedHostnames, parsed.Host);

    /// <summary>True when a CORS <c>Origin</c> header value's host is allowlisted.</summary>
    public static bool OriginAllowed(IReadOnlyCollection<string> allowedHostnames, string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var parsed) &&
        HostnameAllowed(allowedHostnames, parsed.Host);
}

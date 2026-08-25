using Microsoft.EntityFrameworkCore;
using Praxy.Persistence;
using Praxy.Persistence.Entities;

namespace Praxy.Sites;

/// <summary>
/// Exact-match custom-domain lookup against <c>site_domains</c> (Sites Phase 3) — deliberately kept
/// separate from <see cref="SiteHostPattern"/>'s <c>TryParse</c> rather than folded into its signature.
/// <c>TryParse</c> is a pure string-suffix parse against a fixed <c>options.Domain</c> with no DB
/// access, by design (both its callers need a cheap first-pass check before ever touching the
/// database). A custom domain has no fixed suffix or label count to parse against — it's an exact
/// match against a database table, which makes it inherently a DB lookup, not a parse; giving it its
/// own type keeps that distinction visible at every call site instead of hiding a database round trip
/// inside something that looks like a pure function.
///
/// Both callers (<see cref="SiteProxyMiddleware"/> and the <c>_ask-tls</c> endpoint) reach for this
/// exact same lookup rather than each writing their own <c>site_domains</c> query — the same
/// "one shared implementation" discipline <c>SiteHostPattern</c>'s own doc comment asks for, just
/// split into a sibling type instead of a second overload.
/// </summary>
public static class SiteCustomDomainLookup
{
    /// <summary>Case-insensitive, trimmed — matches how <see cref="SiteHostPattern"/> compares hostnames (<c>OrdinalIgnoreCase</c>), stored lowercase so the unique index enforces it too.</summary>
    public static string Normalize(string host) => host.Trim().ToLowerInvariant();

    /// <summary>The raw <c>site_domains</c> row for an exact hostname match, <c>pending</c> or <c>verified</c> — or null if nothing claims this hostname.</summary>
    public static Task<SiteDomain?> FindAsync(PraxyDb db, string host, CancellationToken ct) =>
        db.SiteDomains.AsNoTracking().FirstOrDefaultAsync(d => d.Hostname == Normalize(host), ct);

    /// <summary>
    /// The owning site for a registered custom domain, but only if that site is currently enabled —
    /// this is the strict half of the allow-list both <see cref="SiteProxyMiddleware"/> and
    /// <c>_ask-tls</c> need: a domain belonging to a disabled site must reject exactly like the
    /// built-in subdomain path does, not just fail to serve traffic while still happily minting it a
    /// TLS certificate.
    /// </summary>
    public static async Task<Site?> ResolveEnabledSiteAsync(PraxyDb db, string host, CancellationToken ct)
    {
        var domain = await FindAsync(db, host, ct);
        if (domain is null)
            return null;
        return await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == domain.SiteId && s.Enabled, ct);
    }
}

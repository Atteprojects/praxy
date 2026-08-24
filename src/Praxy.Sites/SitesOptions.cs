namespace Praxy.Sites;

/// <summary>
/// Every knob configurable, per CLAUDE.md's cross-phase rule — bound from <c>Praxy:Sites:*</c>
/// config in Program.cs, same plain-record-of-defaults shape as <c>FunctionsOptions</c>.
/// </summary>
public sealed record SitesOptions(
    string DockerEndpoint = "unix:///var/run/docker.sock",
    string DockerNetwork = "",
    /// <summary>
    /// The wildcard suffix a site's public hostname must end in: <c>&lt;key&gt;.&lt;projectId&gt;.{Domain}</c>.
    /// Defaults to <c>sites.localhost</c> — every modern browser/OS resolver sends that straight to
    /// 127.0.0.1 with no DNS setup, so local dev needs no configuration. A real deployment sets this
    /// to <c>sites.&lt;PRAXY_DOMAIN&gt;</c> (deploy/up.sh derives it automatically).
    /// </summary>
    string Domain = "sites.localhost",
    string NodeBaseImage = "node:22-alpine",
    int BuildPollIntervalSeconds = 2,
    // Next.js builds are heavier than a Functions cold build — npm install plus a full production
    // build of a real app comfortably exceeds Functions' 600s default on a modest host.
    int BuildTimeoutSeconds = 900,
    // Bounds the readiness poll after `docker start` — analogous to Functions'
    // ColdStartTimeoutSeconds, but against the app's own root path rather than a `/_health`
    // contract Sites doesn't control (see SiteDockerExecutor.WaitUntilRespondingAsync).
    int StartupTimeoutSeconds = 60,
    // How often SiteReconciler re-checks that every enabled site's active deployment has a running
    // container — the startup pass alone doesn't catch a container that dies later without Docker's
    // own RestartPolicy bringing it back (e.g. removed out-of-band).
    int ReconcileIntervalSeconds = 60,
    // Next.js SSR servers hold more in memory than a Functions invoke workload — 256MB (Functions'
    // default) undersells a real app.
    long MemoryLimitMb = 512,
    double CpuLimit = 1.0,
    long MaxSourceBytes = 26_214_400,
    // zenika/alpine-chrome: a small (~180MB compressed), actively-maintained, multi-arch
    // (amd64+arm64) headless-Chromium image with a single-invocation CLI screenshot mode — no
    // driver process needed, matching this codebase's existing "ephemeral one-off container"
    // style rather than a long-lived capture service. Pinned to a numbered tag, not `latest`,
    // per the repo's own machine-verified-pins discipline (docs/research/dotnet-stack.md).
    string ScreenshotImage = "zenika/alpine-chrome:124",
    int ScreenshotTimeoutSeconds = 20,
    // Bounded retry, not infinite — a deployment whose container never renders anything
    // meaningful (crashes on every request, etc.) shouldn't be re-attempted forever.
    int ScreenshotMaxAttempts = 3,
    // Cooldown after a failed capture before the same deployment can be reclaimed — found live: a
    // fresh deploy's startup load (image build, migrations, every worker's first run competing at
    // once) can make a cold image pull flaky, and without this a worker polling every
    // BuildPollIntervalSeconds burns through ScreenshotMaxAttempts in well under a minute, with no
    // room for a transient failure to clear before giving up for good.
    int ScreenshotRetryDelaySeconds = 15,
    int ScreenshotWidth = 1280,
    int ScreenshotHeight = 800,
    // How long a preview (non-active) deployment's container may sit with no proxied request
    // before SitePreviewSweeper stops it. Reference point, not a mandate, per
    // docs/handoff/sites-phase-2-prompt.md: Functions' WarmPool defaults to 300s for a much
    // cheaper invoke-shaped workload; a real Next.js SSR server is heavier to cold-start and a
    // developer reviewing a preview leaves real gaps between requests, so this defaults higher.
    int PreviewIdleSeconds = 600,
    // How often SitePreviewSweeper re-scans for idle preview containers — same cadence as
    // SiteReconciler's own ReconcileIntervalSeconds by default, tunable independently.
    int PreviewSweepIntervalSeconds = 60);

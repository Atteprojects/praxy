import { useGithubInstallUrl, useGithubInstallations, useRemoveGithubInstallation } from "../api/vcs";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { DataTable, EmptyState, ErrorNote, FullPageSpinner, PageHeader, Spinner, timeAgo } from "../components/ui";

const HEADERS = ["Account", "Type", "Installation ID", "Connected", ""];

/** GitHub's own mark (Octicons "mark-github", MIT) — a solid glyph, not this file's stroke-icon siblings in ../components/icons.tsx, so it's kept local rather than forced through that shared Icon wrapper's fill:none convention. */
function GithubMark(props: { className?: string }) {
  return (
    <svg viewBox="0 0 16 16" width="16" height="16" fill="currentColor" aria-hidden="true" className={props.className}>
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8Z" />
    </svg>
  );
}

/**
 * Instance-wide GitHub App status — not project-, site-, or function-scoped, even though it lives
 * under a project's nav for shell consistency (the console has no top-level, outside-project route
 * today). Introduced in Sites Phase 4 and reused as-is by Functions git integration — a site or a
 * function connects to a specific repository from its own Settings page once an installation shows up
 * here, and the same repository can be connected to both at once.
 */
export function GitHubSettingsPage() {
  const installations = useGithubInstallations();
  const installUrl = useGithubInstallUrl();
  const removeInstallation = useRemoveGithubInstallation();

  if (installations.isPending) return <FullPageSpinner />;
  if (installations.isError) throw installations.error;

  // Distinct from "no installation yet" (the table below is simply empty) — this is "the instance
  // itself was never pointed at a GitHub App" (Praxy:Vcs:GitHub:AppId/PrivateKey unset), the default
  // state for every fresh self-host until the owner runs the setup in docs/self-host.md.
  const notConfigured = installUrl.error instanceof ApiError && installUrl.error.type === "vcs_github_not_configured";

  const connectButton = (
    <button
      type="button"
      className="btn-ghost border border-ink-700 bg-ink-800 text-ink-100 hover:bg-ink-700 disabled:opacity-40"
      disabled={!installUrl.data}
      onClick={() => {
        if (installUrl.data) window.location.href = installUrl.data.url;
      }}
    >
      {installUrl.isPending ? <Spinner /> : <GithubMark />}
      {installUrl.isPending ? null : "Connect to GitHub"}
    </button>
  );

  return (
    <div>
      <PageHeader
        title="GitHub"
        description="Install Praxy's GitHub App for this instance once, then any project's sites and functions can connect a repository to push-to-deploy. One installation can cover repositories across every project — it isn't tied to the one you're viewing."
        actions={connectButton}
      />

      {notConfigured ? (
        <div className="mb-4">
          <ErrorNote message="This instance's GitHub App isn't set up yet — see docs/self-host.md's Git integration section for the exact steps, then set the five Praxy:Vcs:GitHub:* config values and restart." />
        </div>
      ) : installUrl.isError ? (
        <div className="mb-4">
          <ErrorNote
            message={installUrl.error instanceof ApiError ? installUrl.error.message : "Couldn't load the GitHub install link."}
          />
        </div>
      ) : null}

      {installations.data.total === 0 ? (
        <EmptyState
          headers={HEADERS}
          title="No GitHub App installation connected yet."
          action={connectButton}
        />
      ) : (
        <DataTable headers={HEADERS}>
          {installations.data.installations.map((i) => (
            <tr key={i.id}>
              <td className="px-4 py-3 font-medium text-ink-100">{i.accountLogin}</td>
              <td className="px-4 py-3 text-ink-400">{i.accountType}</td>
              <td className="px-4 py-3 font-mono text-xs text-ink-400">{i.installationId}</td>
              <td className="px-4 py-3 whitespace-nowrap text-ink-400">{timeAgo(i.createdAt)}</td>
              <td className="px-4 py-3 text-right">
                <ConfirmButton
                  label="Disconnect"
                  title="Disconnect GitHub?"
                  confirmLabel="Disconnect"
                  successMessage="Disconnected."
                  body={
                    <>
                      Uninstalls Praxy's GitHub App from <span className="font-mono text-ink-300">{i.accountLogin}</span>.
                      Any site or function still connected to a repository there stops deploying on push — its next
                      build will fail with a clear error until it's reconnected to a working installation. This
                      doesn't touch your code or repositories on GitHub, only the App's access to them.
                    </>
                  }
                  onConfirm={() => removeInstallation.mutateAsync(i.id)}
                />
              </td>
            </tr>
          ))}
        </DataTable>
      )}
    </div>
  );
}

import { useGithubInstallUrl, useGithubInstallations, useRemoveGithubInstallation } from "../api/vcs";
import { ApiError } from "../api/client";
import { ConfirmButton } from "../components/ConfirmButton";
import { DataTable, EmptyState, ErrorNote, FullPageSpinner, PageHeader, Spinner, timeAgo } from "../components/ui";

const HEADERS = ["Account", "Type", "Installation ID", "Connected", ""];

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
      className="btn-primary disabled:opacity-40"
      disabled={!installUrl.data}
      onClick={() => {
        if (installUrl.data) window.location.href = installUrl.data.url;
      }}
    >
      {installUrl.isPending ? <Spinner /> : "Connect GitHub"}
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

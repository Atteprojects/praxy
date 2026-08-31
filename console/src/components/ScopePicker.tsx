/** Praxy.Auth.ApiKeyScopes.All, mirrored here — there is no shared client/server contract to import it from. */
export const ALL_API_KEY_SCOPES = [
  "users.read", "users.write", "teams.read", "teams.write", "databases.read", "databases.write",
  "functions.read", "functions.write", "execution.read", "execution.write",
] as const;

/**
 * The scope checkbox grid ApiKeysPage's create-key modal introduced, extracted so
 * FunctionSettingsPage's "Platform access" section can grant the same ApiKeyScopes to a
 * function's schedule/event executions without a second implementation of this grid.
 */
export function ScopeGrid({
  value,
  onChange,
  scopes = ALL_API_KEY_SCOPES,
}: {
  value: string[];
  onChange: (scopes: string[]) => void;
  scopes?: readonly string[];
}) {
  return (
    <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
      {scopes.map((scope) => (
        <label
          key={scope}
          className={`flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-2 text-sm transition-colors ${
            value.includes(scope)
              ? "border-iris-500/60 bg-iris-500/10 text-ink-100"
              : "border-ink-700 text-ink-400 hover:border-ink-500"
          }`}
        >
          <input
            type="checkbox"
            className="hidden"
            checked={value.includes(scope)}
            onChange={(e) =>
              onChange(e.target.checked ? [...value, scope] : value.filter((s) => s !== scope))
            }
          />
          <span className="font-mono text-xs">{scope}</span>
        </label>
      ))}
    </div>
  );
}

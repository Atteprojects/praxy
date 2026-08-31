'use strict';

/**
 * Scheduled cleanup — deletes rows older than a configurable age from a table you choose. Deployed
 * with a daily cron schedule already set (Console > Functions > this function > Settings shows and
 * edits it); the rest is configuration, not code you need to change to try it out.
 *
 * A schedule-triggered execution has no calling user to inherit a session or JWT from — Praxy only
 * mints PRAXY_FUNCTION_JWT for an execution a specific app user triggered
 * (see docs/handoff/functions-scheduled-credentials-prompt.md for the platform-credentials work this
 * still waits on). Until that lands, this template authenticates with a standing API key instead.
 *
 * Before deploying (or right after — you can always redeploy), set these on this function's Env Vars
 * tab (Console > Functions > this function > Env vars):
 *
 *   PRAXY_ENDPOINT        Praxy's own API base URL, reachable from *inside this function's
 *                         container*, not your browser's:
 *                           - bundled self-host stack: http://api:8080 (function containers share
 *                             the `praxy-functions` Docker network with the api container)
 *                           - `dotnet run` dev instance on Docker Desktop (macOS/Windows):
 *                             http://host.docker.internal:5090
 *   PRAXY_API_KEY         An API key (Console > API Keys) scoped to at least databases.read and
 *                         databases.write, granted access to the target table (permissions, or a
 *                         key with "Bypass permissions" checked).
 *   CLEANUP_DATABASE_ID   The database id to clean up — copy it from the console's URL.
 *   CLEANUP_TABLE_ID      The table id to clean up.
 *
 * Optional:
 *   CLEANUP_DATE_COLUMN   Column to compare against. Defaults to "$createdAt" (every row has this).
 *   CLEANUP_MAX_AGE_DAYS  Rows older than this many days are deleted. Defaults to 30.
 */
module.exports = async () => {
  const endpoint = process.env.PRAXY_ENDPOINT;
  const apiKey = process.env.PRAXY_API_KEY;
  const databaseId = process.env.CLEANUP_DATABASE_ID;
  const tableId = process.env.CLEANUP_TABLE_ID;
  const missing = ['PRAXY_ENDPOINT', 'PRAXY_API_KEY', 'CLEANUP_DATABASE_ID', 'CLEANUP_TABLE_ID']
    .filter((name) => !process.env[name]);
  if (missing.length > 0) {
    return { statusCode: 500, body: `Missing required env var(s): ${missing.join(', ')}. See index.js's header comment.` };
  }

  const dateColumn = process.env.CLEANUP_DATE_COLUMN || '$createdAt';
  const maxAgeDays = Number(process.env.CLEANUP_MAX_AGE_DAYS || '30');
  const cutoff = new Date(Date.now() - maxAgeDays * 24 * 60 * 60 * 1000).toISOString();

  const headers = {
    'content-type': 'application/json',
    'x-praxy-project': process.env.PRAXY_PROJECT_ID,
    'x-praxy-key': apiKey,
  };
  const rowsUrl = `${endpoint}/v1/databases/${databaseId}/tables/${tableId}/rows`;
  const filterQuery = JSON.stringify({ method: 'lessThan', attribute: dateColumn, values: [cutoff] });
  const limitQuery = JSON.stringify({ method: 'limit', values: [100] });
  const listQs = `queries[]=${encodeURIComponent(filterQuery)}&queries[]=${encodeURIComponent(limitQuery)}&total=false`;

  let deleted = 0;
  // Bounded: a stray misconfiguration (e.g. a dateColumn that's never satisfied) must not loop
  // forever inside one execution — 20 pages of up to 100 rows is enough headroom for a real job.
  for (let page = 0; page < 20; page++) {
    const listResponse = await fetch(`${rowsUrl}?${listQs}`, { headers });
    if (!listResponse.ok) {
      return { statusCode: 502, body: `Listing rows failed: ${listResponse.status} ${await listResponse.text()}` };
    }
    const { rows } = await listResponse.json();
    if (rows.length === 0) break;

    for (const row of rows) {
      const deleteResponse = await fetch(`${rowsUrl}/${row['$id']}`, { method: 'DELETE', headers });
      if (deleteResponse.ok) deleted += 1;
    }
  }

  return { statusCode: 200, body: JSON.stringify({ deleted, olderThan: cutoff }) };
};

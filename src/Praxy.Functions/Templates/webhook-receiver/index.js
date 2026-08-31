'use strict';

const crypto = require('crypto');

/**
 * Webhook-style receiver — validates a shared secret carried in the incoming payload, then writes
 * the event to a table you choose.
 *
 * Praxy's invocation endpoint (`POST /v1/functions/{id}/executions`) takes a structured
 * `{ method, path, body }` envelope rather than proxying a raw external request, and a caller's own
 * HTTP headers aren't forwarded into `context.headers` today — so unlike a typical third-party
 * webhook (which signs over a header), this template checks a "secret" field carried in the JSON
 * body itself. Point whatever relays the webhook (a small proxy in front of Praxy, or your own
 * server relaying events it received) at this function with a body shaped like:
 *
 *   { "secret": "<WEBHOOK_SECRET>", "event": "order.created", "data": { ... } }
 *
 * Before deploying (or right after):
 *   1. Grant this function's execute role (Console > Functions > this function > Settings) — "guests"
 *      for a receiver anything on the internet can reach, or a narrower role if only your own signed-in
 *      users relay to it.
 *   2. Set on its Env Vars tab:
 *        WEBHOOK_SECRET          Shared secret the caller must send back in the payload's "secret" field.
 *        PRAXY_ENDPOINT          Praxy's own API base URL reachable from inside this function's
 *                                container — see the scheduled-cleanup template's header comment for
 *                                the self-host/dev values.
 *        RECEIVER_DATABASE_ID    Database id to write received events into.
 *        RECEIVER_TABLE_ID       Table id (needs "event" and "payload" string columns) to write into.
 *        PRAXY_API_KEY           Only needed as a fallback — see below.
 *   3. Grant the table create permission to whichever role your credential resolves to.
 *
 * A request invoked on behalf of a signed-in app user already carries PRAXY_FUNCTION_JWT, which this
 * template prefers when present — PRAXY_API_KEY is only needed for the guest-triggered case a real
 * external webhook relay would actually hit.
 */
module.exports = async (context) => {
  let payload;
  try {
    payload = context.body ? JSON.parse(context.body) : {};
  } catch (e) {
    return { statusCode: 400, body: 'Body must be JSON.' };
  }

  const secret = process.env.WEBHOOK_SECRET;
  if (!secret) return { statusCode: 500, body: 'Missing required env var: WEBHOOK_SECRET.' };
  const given = Buffer.from(String(payload.secret || ''));
  const expected = Buffer.from(secret);
  if (given.length !== expected.length || !crypto.timingSafeEqual(given, expected)) {
    return { statusCode: 401, body: 'Invalid secret.' };
  }

  const endpoint = process.env.PRAXY_ENDPOINT;
  const databaseId = process.env.RECEIVER_DATABASE_ID;
  const tableId = process.env.RECEIVER_TABLE_ID;
  const missing = ['PRAXY_ENDPOINT', 'RECEIVER_DATABASE_ID', 'RECEIVER_TABLE_ID'].filter((name) => !process.env[name]);
  if (missing.length > 0) {
    return { statusCode: 500, body: `Missing required env var(s): ${missing.join(', ')}. See index.js's header comment.` };
  }

  const authHeader = process.env.PRAXY_FUNCTION_JWT
    ? { 'x-praxy-session': process.env.PRAXY_FUNCTION_JWT }
    : process.env.PRAXY_API_KEY
      ? { 'x-praxy-key': process.env.PRAXY_API_KEY }
      : null;
  if (!authHeader) {
    return { statusCode: 500, body: 'No credential available: set PRAXY_API_KEY (or invoke this on behalf of a signed-in user).' };
  }

  const response = await fetch(`${endpoint}/v1/databases/${databaseId}/tables/${tableId}/rows`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-praxy-project': process.env.PRAXY_PROJECT_ID, ...authHeader },
    body: JSON.stringify({
      data: {
        event: String(payload.event ?? 'unknown'),
        payload: JSON.stringify(payload.data ?? {}),
      },
    }),
  });

  if (!response.ok) {
    return { statusCode: 502, body: `Failed to store event: ${response.status} ${await response.text()}` };
  }
  return { statusCode: 200, body: JSON.stringify({ stored: true }) };
};

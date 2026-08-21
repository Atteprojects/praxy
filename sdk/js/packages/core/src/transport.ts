/**
 * The wire-plumbing seam — mirrors `praxy_core`'s `Transport`/`TransportRequest`/
 * `TransportResponse` (`sdk/flutter/praxy_core/lib/src/transport.dart`). `FetchTransport` is the
 * only implementation and must stay edge-runtime-safe: `fetch`, `URL`, `URLSearchParams` only, no
 * `node:*` imports — `praxyMiddleware()` in `@praxy/nextjs` runs on Edge Runtime by default and
 * pulls this package in transitively.
 */

export interface TransportRequest {
  method: string;
  path: string;
  headers?: Record<string, string>;
  /** Repeated query-string params — each value in the array becomes its own `key=value` entry. */
  query?: Record<string, string[]>;
  body?: unknown;
}

export interface TransportResponse {
  status: number;
  /** Lower-cased header names, matching `fetch`'s own `Headers` normalization. */
  headers: Record<string, string>;
  body: string;
}

export interface Transport {
  send(request: TransportRequest): Promise<TransportResponse>;
}

export interface FetchTransportConfig {
  endpoint: string;
}

export class FetchTransport implements Transport {
  private readonly endpoint: string;

  constructor(config: FetchTransportConfig) {
    this.endpoint = config.endpoint.replace(/\/+$/, "");
  }

  async send(request: TransportRequest): Promise<TransportResponse> {
    const url = new URL(this.endpoint + request.path);
    for (const [key, values] of Object.entries(request.query ?? {})) {
      for (const value of values) url.searchParams.append(key, value);
    }

    const headers = { ...request.headers };
    const hasBody = request.body !== undefined;
    if (hasBody) headers["content-type"] = "application/json";

    // A raw fetch failure (DNS/connection/TLS/timeout) propagates as-is — `Praxy.request()`
    // wraps whatever any `Transport` implementation throws as `PraxyNetworkError`, so there is
    // no transport-specific wrapping to do here.
    const response = await fetch(url, {
      method: request.method,
      headers,
      body: hasBody ? JSON.stringify(request.body) : undefined,
    });

    const responseHeaders: Record<string, string> = {};
    response.headers.forEach((value, key) => {
      responseHeaders[key.toLowerCase()] = value;
    });

    return {
      status: response.status,
      headers: responseHeaders,
      body: await response.text(),
    };
  }
}

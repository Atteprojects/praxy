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
  /**
   * A raw byte body — a storage upload, where the bytes *are* the request. Never set together with
   * `body`; a `Transport` sends these verbatim rather than JSON-encoding anything.
   */
  bodyBytes?: Uint8Array;
  /** The `content-type` for `bodyBytes`. Meaningless for a JSON `body`, which is always `application/json`. */
  contentType?: string;
  /**
   * `"bytes"` for a response whose body is a file rather than JSON (a storage download): the
   * response arrives in `bodyBytes` and `body` is left empty, since decoding arbitrary bytes as
   * text would corrupt them.
   */
  expect?: "text" | "bytes";
}

export interface TransportResponse {
  status: number;
  /** Lower-cased header names, matching `fetch`'s own `Headers` normalization. */
  headers: Record<string, string>;
  body: string;
  /** Present only for a request that asked for `expect: "bytes"` and succeeded. */
  bodyBytes?: Uint8Array;
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
    const hasBytes = request.bodyBytes !== undefined;
    const hasBody = !hasBytes && request.body !== undefined;
    if (hasBytes) headers["content-type"] = request.contentType ?? "application/octet-stream";
    else if (hasBody) headers["content-type"] = "application/json";

    // A raw fetch failure (DNS/connection/TLS/timeout) propagates as-is — `Praxy.request()`
    // wraps whatever any `Transport` implementation throws as `PraxyNetworkError`, so there is
    // no transport-specific wrapping to do here.
    const response = await fetch(url, {
      method: request.method,
      headers,
      // Passed straight through: `fetch` takes a BufferSource, so the bytes go on the wire
      // unencoded. The cast only narrows the generic `Uint8Array` to the ArrayBuffer-backed form
      // `BodyInit` names.
      body: hasBytes
        ? (request.bodyBytes as Uint8Array<ArrayBuffer>)
        : hasBody
          ? JSON.stringify(request.body)
          : undefined,
    });

    const responseHeaders: Record<string, string> = {};
    response.headers.forEach((value, key) => {
      responseHeaders[key.toLowerCase()] = value;
    });

    // An error response is always the JSON envelope, whatever the request asked for — so a failed
    // `expect: "bytes"` call still reads its body as text and maps to the same typed error.
    if (request.expect === "bytes" && response.ok) {
      return {
        status: response.status,
        headers: responseHeaders,
        body: "",
        bodyBytes: new Uint8Array(await response.arrayBuffer()),
      };
    }

    return {
      status: response.status,
      headers: responseHeaders,
      body: await response.text(),
    };
  }
}

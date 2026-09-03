import type { Transport, TransportRequest, TransportResponse } from "../../src/transport";

export type ResponseBuilder = (request: TransportRequest) => TransportResponse;

/**
 * A `Transport` test double: replays a handler's response instead of hitting the network, and
 * records every request sent through it for assertions. Mirrors `praxy_core`'s
 * `test/support/fake_transport.dart`.
 */
export class FakeTransport implements Transport {
  readonly requests: TransportRequest[] = [];

  constructor(private readonly handler: ResponseBuilder) {}

  async send(request: TransportRequest): Promise<TransportResponse> {
    this.requests.push(request);
    return this.handler(request);
  }
}

export function jsonResponse(status: number, body: unknown, headers: Record<string, string> = {}): TransportResponse {
  return { status, headers, body: body === undefined ? "" : JSON.stringify(body) };
}

export function emptyResponse(status: number, headers: Record<string, string> = {}): TransportResponse {
  return { status, headers, body: "" };
}

/** A binary body, as `FetchTransport` returns one for a request that asked for `expect: "bytes"`. */
export function bytesResponse(
  status: number,
  bytes: Uint8Array,
  headers: Record<string, string> = {},
): TransportResponse {
  return { status, headers, body: "", bodyBytes: bytes };
}

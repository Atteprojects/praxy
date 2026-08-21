import type { Transport, TransportRequest, TransportResponse } from "@praxy/core";

export type ResponseBuilder = (request: TransportRequest) => TransportResponse;

/** Mirrors `@praxy/core`'s own `test/support/fake-transport.ts` — duplicated locally, same reason as the other packages. */
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

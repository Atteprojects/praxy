import { describe, expect, it } from "vitest";
import { Praxy } from "../src/client";
import {
  PraxyApiError,
  PraxyAuthError,
  PraxyConflictError,
  PraxyDecodeError,
  PraxyNetworkError,
  PraxyNotFoundError,
  PraxyRateLimitError,
  PraxyValidationError,
} from "../src/errors";
import type { TransportRequest } from "../src/transport";
import { emptyResponse, FakeTransport, jsonResponse } from "./support/fake-transport";

function envelope(
  status: number,
  overrides: Partial<{ type: string; fields: Record<string, string[]> }> = {},
) {
  return {
    message: "Something went wrong.",
    code: status,
    type: overrides.type ?? "general_bad_request",
    version: "v1",
    requestId: "req_1",
    fields: overrides.fields,
  };
}

describe("Praxy client — headers", () => {
  it("always sends X-Praxy-Project, and X-Praxy-Session only once a session token is set", async () => {
    let captured!: TransportRequest;
    const transport = new FakeTransport((req) => {
      captured = req;
      return jsonResponse(200, { ok: true });
    });

    const anonymous = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    await anonymous.request("GET", "/v1/account");
    expect(captured.headers?.["X-Praxy-Project"]).toBe("proj_1");
    expect(captured.headers?.["X-Praxy-Session"]).toBeUndefined();

    const authed = new Praxy({
      endpoint: "https://api.test",
      projectId: "proj_1",
      sessionToken: "secret-token",
      transport,
    });
    await authed.request("GET", "/v1/account");
    expect(captured.headers?.["X-Praxy-Session"]).toBe("secret-token");
  });

  it("sends X-Praxy-Key instead of X-Praxy-Session when constructed with an apiKey", async () => {
    let captured!: TransportRequest;
    const transport = new FakeTransport((req) => {
      captured = req;
      return jsonResponse(200, { ok: true });
    });
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", apiKey: "key_1", transport });
    await client.request("GET", "/v1/account");
    expect(captured.headers?.["X-Praxy-Key"]).toBe("key_1");
    expect(captured.headers?.["X-Praxy-Session"]).toBeUndefined();
  });
});

describe("Praxy client — response decoding", () => {
  it("decodes a 200 JSON body", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, { name: "Ada" }));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    await expect(client.request<{ name: string }>("GET", "/x")).resolves.toEqual({ name: "Ada" });
  });

  it("resolves undefined for a 204 with no body", async () => {
    const transport = new FakeTransport(() => emptyResponse(204));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    await expect(client.request("DELETE", "/x")).resolves.toBeUndefined();
  });

  it("throws PraxyDecodeError for a malformed success body instead of a raw parse error", async () => {
    const transport = new FakeTransport(() => ({ status: 200, headers: {}, body: "{not json" }));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    await expect(client.request("GET", "/x")).rejects.toBeInstanceOf(PraxyDecodeError);
  });
});

describe("Praxy client — error mapping by status", () => {
  const cases: Array<[number, unknown]> = [
    [401, PraxyAuthError],
    [403, PraxyAuthError],
    [404, PraxyNotFoundError],
    [409, PraxyConflictError],
  ];

  for (const [status, ctor] of cases) {
    it(`maps ${status} to ${(ctor as { name: string }).name}`, async () => {
      const transport = new FakeTransport(() => jsonResponse(status, envelope(status)));
      const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
      await expect(client.request("GET", "/x")).rejects.toBeInstanceOf(ctor as new (...args: never[]) => Error);
    });
  }

  it("maps 429 to PraxyRateLimitError and carries Retry-After", async () => {
    const transport = new FakeTransport(() => jsonResponse(429, envelope(429), { "retry-after": "12" }));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    const error = await client.request("GET", "/x").catch((e) => e);
    expect(error).toBeInstanceOf(PraxyRateLimitError);
    expect((error as PraxyRateLimitError).retryAfter).toBe(12);
  });

  it("maps 400 with a fields map to PraxyValidationError", async () => {
    const transport = new FakeTransport(() =>
      jsonResponse(400, envelope(400, { fields: { email: ["Required."] } })),
    );
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    const error = await client.request("GET", "/x").catch((e) => e);
    expect(error).toBeInstanceOf(PraxyValidationError);
    expect((error as PraxyValidationError).fields).toEqual({ email: ["Required."] });
  });

  it("maps 400 without a fields map to the base PraxyApiError, not PraxyValidationError", async () => {
    const transport = new FakeTransport(() => jsonResponse(400, envelope(400)));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    const error = await client.request("GET", "/x").catch((e) => e);
    expect(error).toBeInstanceOf(PraxyApiError);
    expect(error).not.toBeInstanceOf(PraxyValidationError);
  });

  it("still produces a usable PraxyApiError when the error body itself is malformed", async () => {
    const transport = new FakeTransport(() => ({
      status: 500,
      headers: { "x-praxy-request-id": "req_9" },
      body: "not json at all",
    }));
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    const error = await client.request("GET", "/x").catch((e) => e);
    expect(error).toBeInstanceOf(PraxyApiError);
    expect((error as PraxyApiError).requestId).toBe("req_9");
  });

  it("wraps a transport-level failure as PraxyNetworkError, preserving the cause", async () => {
    const cause = new Error("DNS lookup failed");
    const transport = new FakeTransport(() => {
      throw cause;
    });
    const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
    const error = await client.request("GET", "/x").catch((e) => e);
    expect(error).toBeInstanceOf(PraxyNetworkError);
  });
});

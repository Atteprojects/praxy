import { describe, expect, it } from "vitest";
import { Praxy } from "../src/client";
import type { TransportRequest } from "../src/transport";
import { jsonResponse, FakeTransport } from "./support/fake-transport";

function clientCapturing(response: ReturnType<typeof jsonResponse>) {
  let captured!: TransportRequest;
  const transport = new FakeTransport((req) => {
    captured = req;
    return response;
  });
  const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
  return { client, captured: () => captured };
}

const execution = {
  id: "e1",
  trigger: "http",
  async: false,
  status: "completed",
  method: "GET",
  path: "/",
  statusCode: 200,
  responseBody: "ok",
  logs: "",
  errors: null,
  durationMs: 12,
  coldStart: false,
  triggeredBy: "user:u1",
  createdAt: "t",
  completedAt: "t",
};

describe("FunctionsService", () => {
  it("createExecution() → POST /v1/functions/{id}/executions, sync by default", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, execution));
    await client.functions.createExecution("fn_1", { method: "GET", path: "/hello" });
    expect(captured().method).toBe("POST");
    expect(captured().path).toBe("/v1/functions/fn_1/executions");
    expect(captured().query).toBeUndefined();
    expect(captured().body).toEqual({ method: "GET", path: "/hello" });
  });

  it("createExecution({ async: true }) sets ?async=true and omits async from the body", async () => {
    const { client, captured } = clientCapturing(jsonResponse(202, { ...execution, status: "waiting" }));
    await client.functions.createExecution("fn_1", { async: true });
    expect(captured().query).toEqual({ async: ["true"] });
    expect(captured().body).toEqual({});
  });

  it("getExecution() → GET /v1/functions/{id}/executions/{eid}", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, execution));
    const result = await client.functions.getExecution("fn_1", "e1");
    expect(captured().method).toBe("GET");
    expect(captured().path).toBe("/v1/functions/fn_1/executions/e1");
    expect(result.id).toBe("e1");
  });
});

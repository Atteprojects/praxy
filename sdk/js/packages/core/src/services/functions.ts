import type { Praxy } from "../client.js";
import type { FunctionExecution } from "../models.js";

/**
 * Data-plane function invocation only (`/v1/functions`) — deployment management stays a
 * console/operator concern. 2 methods, matching `praxy_core`'s `FunctionsService`
 * (`sdk/flutter/praxy_core/lib/src/services/functions_service.dart`).
 */
export class FunctionsService {
  constructor(private readonly client: Praxy) {}

  /** Sync by default; pass `async: true` to get a 202 receipt back immediately (poll with `getExecution`). */
  createExecution(
    functionId: string,
    input: { method?: string; path?: string; body?: string; async?: boolean } = {},
  ): Promise<FunctionExecution> {
    const { async, ...body } = input;
    return this.client.request<FunctionExecution>(
      "POST",
      `/v1/functions/${encodeURIComponent(functionId)}/executions`,
      { query: async ? { async: ["true"] } : undefined, body },
    );
  }

  /** Scoped to the caller's own execution — another caller's execution id reads back as a 404. */
  getExecution(functionId: string, executionId: string): Promise<FunctionExecution> {
    return this.client.request<FunctionExecution>(
      "GET",
      `/v1/functions/${encodeURIComponent(functionId)}/executions/${encodeURIComponent(executionId)}`,
    );
  }
}

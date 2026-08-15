import type { ErrorEnvelope } from "./types";

export class ApiError extends Error {
  constructor(public readonly envelope: ErrorEnvelope) {
    super(envelope.message);
    this.name = "ApiError";
  }

  get type(): string {
    return this.envelope.type;
  }

  get code(): number {
    return this.envelope.code;
  }

  fieldErrors(field: string): string[] {
    return this.envelope.fields?.[field] ?? [];
  }
}

/** Session transport is the httpOnly cookie; same origin in prod, Vite proxy in dev. */
export async function api<T>(
  path: string,
  options: { method?: string; body?: unknown } = {},
): Promise<T> {
  const response = await fetch(`/v1${path}`, {
    method: options.method ?? "GET",
    credentials: "include",
    headers: options.body !== undefined ? { "Content-Type": "application/json" } : undefined,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
  });

  if (!response.ok) {
    let envelope: ErrorEnvelope;
    try {
      envelope = (await response.json()) as ErrorEnvelope;
    } catch {
      envelope = {
        message: `Request failed with status ${response.status}`,
        code: response.status,
        type: "general_server_error",
        version: "",
        requestId: response.headers.get("X-Praxy-Request-Id") ?? "",
      };
    }
    throw new ApiError(envelope);
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

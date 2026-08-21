/**
 * Typed error hierarchy mirroring `praxy_core`'s sealed `PraxyException` (see
 * `sdk/flutter/praxy_core/lib/src/errors.dart`) — same shape, ported to TS classes since the
 * language has no sealed-class exhaustiveness check. Each subclass carries a `kind` discriminant
 * so callers can `switch (error.kind)` without `instanceof` chains.
 */

export interface ErrorEnvelope {
  message: string;
  code: number;
  type: string;
  version: string;
  requestId: string;
  fields?: Record<string, string[]>;
}

export abstract class PraxyError extends Error {
  abstract readonly kind: string;
}

/** Any server error response — carries the wire envelope's `type`/`requestId` through. */
export class PraxyApiError extends PraxyError {
  readonly kind: string = "api";
  readonly status: number;
  readonly type: string;
  readonly requestId: string;

  constructor(envelope: ErrorEnvelope) {
    super(envelope.message);
    this.name = "PraxyApiError";
    this.status = envelope.code;
    this.type = envelope.type;
    this.requestId = envelope.requestId;
  }
}

export class PraxyAuthError extends PraxyApiError {
  override readonly kind = "auth";
  constructor(envelope: ErrorEnvelope) {
    super(envelope);
    this.name = "PraxyAuthError";
  }
}

export class PraxyNotFoundError extends PraxyApiError {
  override readonly kind = "not_found";
  constructor(envelope: ErrorEnvelope) {
    super(envelope);
    this.name = "PraxyNotFoundError";
  }
}

export class PraxyConflictError extends PraxyApiError {
  override readonly kind = "conflict";
  constructor(envelope: ErrorEnvelope) {
    super(envelope);
    this.name = "PraxyConflictError";
  }
}

export class PraxyRateLimitError extends PraxyApiError {
  override readonly kind = "rate_limit";
  readonly retryAfter: number | null;
  constructor(envelope: ErrorEnvelope, retryAfter: number | null) {
    super(envelope);
    this.name = "PraxyRateLimitError";
    this.retryAfter = retryAfter;
  }
}

/** 400 with a structured field-error map (`fields`). */
export class PraxyValidationError extends PraxyApiError {
  override readonly kind = "validation";
  readonly fields: Record<string, string[]>;
  constructor(envelope: ErrorEnvelope, fields: Record<string, string[]>) {
    super(envelope);
    this.name = "PraxyValidationError";
    this.fields = fields;
  }
}

/** Transport-level failure — network/DNS/timeout, never a server response. */
export class PraxyNetworkError extends PraxyError {
  readonly kind = "network";
  readonly cause: unknown;
  constructor(message: string, cause: unknown) {
    super(message);
    this.name = "PraxyNetworkError";
    this.cause = cause;
  }
}

/** A response body that wasn't the expected shape (malformed JSON, missing field). */
export class PraxyDecodeError extends PraxyError {
  readonly kind = "decode";
  constructor(message: string) {
    super(message);
    this.name = "PraxyDecodeError";
  }
}

/**
 * Maps an HTTP status code + decoded error envelope + response headers to the matching typed
 * subclass. Switches on the actual HTTP status (mirrors `praxy_core`'s `Praxy._mapError`), not
 * `envelope.code` — the two agree in practice, but the wire status is the source of truth.
 */
export function mapApiError(
  status: number,
  envelope: ErrorEnvelope,
  headers: Record<string, string>,
): PraxyApiError {
  switch (status) {
    case 401:
    case 403:
      return new PraxyAuthError(envelope);
    case 404:
      return new PraxyNotFoundError(envelope);
    case 409:
      return new PraxyConflictError(envelope);
    case 429:
      return new PraxyRateLimitError(envelope, parseRetryAfter(headers));
    default:
      if (envelope.fields && Object.keys(envelope.fields).length > 0) {
        return new PraxyValidationError(envelope, envelope.fields);
      }
      return new PraxyApiError(envelope);
  }
}

function parseRetryAfter(headers: Record<string, string>): number | null {
  const raw = headers["retry-after"];
  if (!raw) return null;
  const seconds = Number(raw);
  return Number.isFinite(seconds) ? seconds : null;
}

import { describe, expect, it } from "vitest";
import { Col, Query } from "../src/query";

describe("Query", () => {
  it("encodes a simple filter as {method, attribute, values}", () => {
    const title = new Col<string>("title");
    expect(Query.equal(title, "Hello").toJSON()).toEqual({
      method: "equal",
      attribute: "title",
      values: ["Hello"],
    });
  });

  it("encodes limit/offset without an attribute", () => {
    expect(Query.limit(50).toJSON()).toEqual({ method: "limit", values: [50] });
    expect(Query.offset(10).toJSON()).toEqual({ method: "offset", values: [10] });
  });

  it("encodes isNull/isNotNull without values", () => {
    const deletedAt = new Col<string | null>("deletedAt");
    expect(Query.isNull(deletedAt).toJSON()).toEqual({ method: "isNull", attribute: "deletedAt" });
  });

  it("encodes and()/or() by composing child Query values, not re-parsed strings", () => {
    const status = new Col<string>("status");
    const owner = new Col<string>("owner");
    const combined = Query.or([Query.equal(status, "open"), Query.equal(owner, "me")]);
    expect(combined.toJSON()).toEqual({
      method: "or",
      values: [
        { method: "equal", attribute: "status", values: ["open"] },
        { method: "equal", attribute: "owner", values: ["me"] },
      ],
    });
  });

  it("encodes a Date value as UTC ISO-8601", () => {
    const createdAt = new Col<Date>("createdAt");
    const date = new Date("2026-01-01T00:00:00.000Z");
    expect(Query.equal(createdAt, date).toJSON()).toEqual({
      method: "equal",
      attribute: "createdAt",
      values: ["2026-01-01T00:00:00.000Z"],
    });
  });

  it("encode() returns one JSON string suitable for a repeated queries[] param", () => {
    const title = new Col<string>("title");
    expect(Query.equal(title, "Hello").encode()).toBe(
      JSON.stringify({ method: "equal", attribute: "title", values: ["Hello"] }),
    );
  });

  it("select() lists column names as values with no attribute", () => {
    const id = new Col<string>("$id");
    const title = new Col<string>("title");
    expect(Query.select([id, title]).toJSON()).toEqual({ method: "select", values: ["$id", "title"] });
  });
});

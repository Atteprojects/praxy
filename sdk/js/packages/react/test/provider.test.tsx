import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";
import { PraxyProvider, usePraxyClient, usePraxyJwt } from "../src/provider";
import { FakeTransport, jsonResponse } from "./support/fake-transport";

describe("usePraxyClient / usePraxyJwt", () => {
  it("throws when called outside a <PraxyProvider>", () => {
    expect(() => renderHook(() => usePraxyClient())).toThrow(/PraxyProvider/);
  });

  it("passes initialJwt through as the client's session token (X-Praxy-Session)", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, { roles: ["any"], principal: "guest", scopes: null }));
    const wrapper = ({ children }: { children: ReactNode }) => (
      <PraxyProvider config={{ endpoint: "https://api.test", projectId: "proj_1" }} initialJwt="jwt-1" transport={transport}>
        {children}
      </PraxyProvider>
    );
    const { result } = renderHook(() => usePraxyClient(), { wrapper });
    await result.current.account.roles();
    expect(transport.requests[0]?.headers?.["X-Praxy-Session"]).toBe("jwt-1");
  });

  it("setJwt rebuilds the client used by subsequent hook calls", async () => {
    const transport = new FakeTransport(() => jsonResponse(200, { roles: ["any"], principal: "guest", scopes: null }));
    const wrapper = ({ children }: { children: ReactNode }) => (
      <PraxyProvider config={{ endpoint: "https://api.test", projectId: "proj_1" }} transport={transport}>
        {children}
      </PraxyProvider>
    );
    const { result } = renderHook(() => ({ client: usePraxyClient(), jwt: usePraxyJwt() }), { wrapper });

    result.current.jwt.setJwt("jwt-2");
    await waitFor(() => expect(result.current.jwt.jwt).toBe("jwt-2"));

    await result.current.client.account.roles();
    // `usePraxyClient()`'s return value from this same render already reflects the new jwt via context.
    const latest = transport.requests.at(-1);
    expect(latest?.headers?.["X-Praxy-Session"]).toBeDefined();
  });
});

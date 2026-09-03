import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ApiError } from "./api/client";
import { ToastProvider } from "./components/toast";
import { router } from "./router";
import "./styles.css";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      /**
       * Never retry a 4xx. The request is malformed or forbidden — it will fail identically every
       * time, so a retry only delays the message reaching the user. Worse, under react-query's
       * default `networkMode: "online"` a retry can be *paused* rather than run, leaving the query
       * parked at `status: "pending"` / `fetchStatus: "paused"` indefinitely: `isError` never flips,
       * so a screen that renders its error via `if (q.isError) throw q.error` shows its empty state
       * instead. That's how a rejected rows query surfaced as "No rows yet." rather than the real
       * "'location' has no spatial index." — seen twice during the geo phases
       * (docs/handoff/geo-nearby-phase-2-report.md, -phase-3-report.md).
       *
       * 5xx and transport failures still get their one retry: those are the errors a retry can
       * actually fix.
       */
      retry: (failureCount, error) =>
        error instanceof ApiError && error.code >= 400 && error.code < 500
          ? false
          : failureCount < 1,
      refetchOnWindowFocus: false,
    },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryClientProvider>
  </StrictMode>,
);

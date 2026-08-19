import { useQuery } from "@tanstack/react-query";
import { api } from "./client";
import type { AuditLogList } from "./types";

export interface AuditLogFilters {
  action?: string;
  actor?: string;
  resource?: string;
  from?: string;
  to?: string;
  offset?: number;
  limit?: number;
}

function toQuery(filters: AuditLogFilters): string {
  const params = new URLSearchParams();
  if (filters.action) params.set("action", filters.action);
  if (filters.actor) params.set("actor", filters.actor);
  if (filters.resource) params.set("resource", filters.resource);
  if (filters.from) params.set("from", filters.from);
  if (filters.to) params.set("to", filters.to);
  if (filters.offset) params.set("offset", String(filters.offset));
  if (filters.limit) params.set("limit", String(filters.limit));
  const qs = params.toString();
  return qs ? `?${qs}` : "";
}

export function useProjectAuditLog(projectId: string, filters: AuditLogFilters) {
  return useQuery({
    queryKey: ["projects", projectId, "audit", filters],
    queryFn: () => api<AuditLogList>(`/console/projects/${projectId}/audit${toQuery(filters)}`),
  });
}

export function useInstanceAuditLog(filters: AuditLogFilters) {
  return useQuery({
    queryKey: ["console", "audit", filters],
    queryFn: () => api<AuditLogList>(`/console/audit${toQuery(filters)}`),
  });
}

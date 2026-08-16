import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type {
  CreatedWebhook, Webhook, WebhookDeliveryDetail, WebhookDeliveryList, WebhookList,
} from "./types";

const base = (projectId: string) => `/console/projects/${projectId}/webhooks`;

export function useWebhooks(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "webhooks"],
    queryFn: () => api<WebhookList>(base(projectId)),
  });
}

export function useWebhook(projectId: string, webhookId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "webhooks", webhookId],
    queryFn: () => api<Webhook>(`${base(projectId)}/${webhookId}`),
  });
}

export function useCreateWebhook(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; url: string; events: string[] }) =>
      api<CreatedWebhook>(base(projectId), { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "webhooks"] }),
  });
}

export function useUpdateWebhook(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ webhookId, ...input }: {
      webhookId: string; name?: string; url?: string; events?: string[]; enabled?: boolean;
    }) => api<Webhook>(`${base(projectId)}/${webhookId}`, { method: "PATCH", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "webhooks"] }),
  });
}

export function useDeleteWebhook(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (webhookId: string) =>
      api<void>(`${base(projectId)}/${webhookId}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "webhooks"] }),
  });
}

export function useWebhookDeliveries(projectId: string, webhookId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "webhooks", webhookId, "deliveries"],
    queryFn: () => api<WebhookDeliveryList>(`${base(projectId)}/${webhookId}/deliveries`),
    refetchInterval: 4_000,
  });
}

export function useWebhookDelivery(projectId: string, webhookId: string, deliveryId: string | null) {
  return useQuery({
    queryKey: ["projects", projectId, "webhooks", webhookId, "deliveries", deliveryId],
    queryFn: () => api<WebhookDeliveryDetail>(`${base(projectId)}/${webhookId}/deliveries/${deliveryId}`),
    enabled: deliveryId !== null,
  });
}

export function useRedeliverWebhook(projectId: string, webhookId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (deliveryId: string) =>
      api<{ id: string }>(`${base(projectId)}/${webhookId}/deliveries/${deliveryId}/redeliver`, { method: "POST" }),
    onSuccess: () =>
      queryClient.invalidateQueries({ queryKey: ["projects", projectId, "webhooks", webhookId, "deliveries"] }),
  });
}

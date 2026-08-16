import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type {
  AuthTemplateKey, MessageDetail, MessageList, MessagingProvider, MessagingProviderList,
  MessagingSubscriberList, MessagingTemplate, MessagingTemplateList, MessagingTopic, MessagingTopicList,
} from "./types";

const base = (projectId: string) => `/console/projects/${projectId}/messaging`;

// ---- providers ----

export function useMessagingProviders(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "providers"],
    queryFn: () => api<MessagingProviderList>(`${base(projectId)}/providers`),
  });
}

export interface ProviderInput {
  type: string;
  name: string;
  host: string;
  port: number;
  username?: string;
  from: string;
  useTls: boolean;
  secret?: string;
  isDefault?: boolean;
}

export function useCreateProvider(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: ProviderInput) => api<MessagingProvider>(`${base(projectId)}/providers`, { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "providers"] }),
  });
}

export interface ProviderUpdateInput extends Partial<ProviderInput> {
  clearSecret?: boolean;
  enabled?: boolean;
}

export function useUpdateProvider(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ providerId, ...input }: { providerId: string } & ProviderUpdateInput) =>
      api<MessagingProvider>(`${base(projectId)}/providers/${providerId}`, { method: "PATCH", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "providers"] }),
  });
}

export function useDeleteProvider(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (providerId: string) => api<void>(`${base(projectId)}/providers/${providerId}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "providers"] }),
  });
}

// ---- topics ----

export function useMessagingTopics(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "topics"],
    queryFn: () => api<MessagingTopicList>(`${base(projectId)}/topics`),
  });
}

export function useMessagingTopic(projectId: string, topicId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "topics", topicId],
    queryFn: () => api<MessagingTopic>(`${base(projectId)}/topics/${topicId}`),
  });
}

export function useCreateTopic(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { key: string; name: string; description?: string }) =>
      api<MessagingTopic>(`${base(projectId)}/topics`, { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics"] }),
  });
}

export function useDeleteTopic(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (topicId: string) => api<void>(`${base(projectId)}/topics/${topicId}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics"] }),
  });
}

export function useTopicSubscribers(projectId: string, topicId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "topics", topicId, "subscribers"],
    queryFn: () => api<MessagingSubscriberList>(`${base(projectId)}/topics/${topicId}/subscribers`),
  });
}

export function useSubscribe(projectId: string, topicId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) =>
      api(`${base(projectId)}/topics/${topicId}/subscribers`, { method: "POST", body: { userId } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics", topicId, "subscribers"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics"] });
    },
  });
}

export function useUnsubscribe(projectId: string, topicId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (subscriberId: string) =>
      api<void>(`${base(projectId)}/topics/${topicId}/subscribers/${subscriberId}`, { method: "DELETE" }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics", topicId, "subscribers"] });
      void queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "topics"] });
    },
  });
}

// ---- templates ----

export function useMessagingTemplates(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "templates"],
    queryFn: () => api<MessagingTemplateList>(`${base(projectId)}/templates`),
  });
}

export function useSetTemplate(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ key, subject, body }: { key: AuthTemplateKey; subject: string; body: string }) =>
      api<MessagingTemplate>(`${base(projectId)}/templates/${key}`, { method: "PUT", body: { subject, body } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "templates"] }),
  });
}

export function useResetTemplate(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (key: AuthTemplateKey) => api<MessagingTemplate>(`${base(projectId)}/templates/${key}`, { method: "DELETE" }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "templates"] }),
  });
}

// ---- messages ----

export function useMessages(projectId: string) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "messages"],
    queryFn: () => api<MessageList>(`${base(projectId)}/messages`),
    refetchInterval: 3_000,
  });
}

export function useMessage(projectId: string, messageId: string | null) {
  return useQuery({
    queryKey: ["projects", projectId, "messaging", "messages", messageId],
    queryFn: () => api<MessageDetail>(`${base(projectId)}/messages/${messageId}`),
    enabled: messageId !== null,
    refetchInterval: (query) => (query.state.data?.message.status === "processing" ? 1_000 : false),
  });
}

export function useSendMessage(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { subject: string; body: string; topicIds: string[]; userIds: string[] }) =>
      api<{ id: string }>(`${base(projectId)}/messages`, { method: "POST", body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["projects", projectId, "messaging", "messages"] }),
  });
}

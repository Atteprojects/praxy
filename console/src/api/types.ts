export interface ErrorEnvelope {
  message: string;
  code: number;
  type: string;
  version: string;
  requestId: string;
  fields?: Record<string, string[]>;
}

export interface Capabilities {
  version: string;
  claimed: boolean;
  setupTokenRequired: boolean;
  features: {
    auth: boolean;
    databases: boolean;
    realtime: boolean;
    messaging: boolean;
    functions: boolean;
    webhooks: boolean;
  };
}

export interface Account {
  id: string;
  email: string;
  name: string;
  createdAt: string;
}

export interface Project {
  id: string;
  name: string;
  organizationId: string | null;
  lastPingAt?: string | null;
  createdAt: string;
}

export interface ProjectList {
  total: number;
  projects: Project[];
}

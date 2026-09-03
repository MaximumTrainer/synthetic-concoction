// Types matching the Fabricate REST API

export interface Account {
  id: string;
  name: string;
  createdAt: string;
}

export interface AccountMembership {
  accountId: string;
  userId: string;
  role: "Member" | "Owner";
  joinedAt: string;
}

export interface Workspace {
  id: string;
  accountId: string;
  name: string;
  createdAt: string;
}

export interface Project {
  id: string;
  workspaceId: string;
  name: string;
  isArchived: boolean;
  createdAt: string;
}

export interface DatasetRun {
  id: string;
  status: "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  seed: number;
  requestedRowCounts: Record<string, number>;
}

export interface ApiKey {
  id: string;
  displayName: string;
  prefix: string;
  scopes: string[];
  expiresAt: string | null;
  isRevoked: boolean;
  createdAt: string;
}

export interface ApiKeyCreateResult {
  key: ApiKey;
  /** Plaintext secret — shown only once. */
  secret: string;
}

export interface ChatSession {
  id: string;
  workspaceId: string;
  title: string;
  mode: string;
  archivedAt: string | null;
  createdAt: string;
}

export interface ChatMessage {
  id: string;
  sessionId: string;
  role: "user" | "assistant" | "tool";
  content: string;
  createdAt: string;
}

export interface Workflow {
  id: string;
  workspaceId: string;
  name: string;
  isDisabled: boolean;
  createdAt: string;
}

export interface PaginatedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface FabricateClientOptions {
  /** Base URL of the Fabricate API, e.g. https://api.example.com */
  baseUrl: string;
  /** API key for authentication (sent as X-Api-Key header) */
  apiKey: string;
  /** Optional fetch implementation — defaults to global fetch */
  fetch?: typeof globalThis.fetch;
}

export type ComplianceProfile = "Default" | "Healthcare" | "Finance";

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

// ─── Chat ─────────────────────────────────────────────────────────────────────

/** How the agent treats tool calls the model makes. */
export type ChatMode = "Guided" | "Autonomous" | "ReviewRequired";

export interface ChatSession {
  id: string;
  workspaceId: string;
  projectId: string | null;
  userId: string;
  name: string;
  mode: ChatMode;
  isArchived: boolean;
  createdAt: string;
  archivedAt: string | null;
  instructionOverride: string | null;
}

/** `System` messages are notices from Fabricate itself (a declined request, a missing credential). */
export type MessageRole = "User" | "Assistant" | "System" | "Tool";

export interface ChatMessage {
  id: string;
  sessionId: string;
  role: MessageRole;
  content: string;
  createdAt: string;
}

export type ToolInvocationStatus = "Pending" | "Running" | "Succeeded" | "Failed";

export interface ToolInvocation {
  id: string;
  sessionId: string;
  messageId: string | null;
  toolName: string;
  inputJson: string | null;
  outputJson: string | null;
  status: ToolInvocationStatus;
  startedAt: string;
  completedAt: string | null;
  errorMessage: string | null;
}

export interface TokenUsage {
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
}

export type LlmStopReason = "EndTurn" | "ToolUse" | "MaxTokens" | "Refusal" | "ContentFiltered" | "Error";

/** Everything one user message produced. `assistantMessage` is a `System` notice on refusal, failure, or a missing credential. */
export interface ChatTurnResult {
  userMessage: ChatMessage;
  assistantMessage: ChatMessage | null;
  toolInvocations: ToolInvocation[];
  usage: TokenUsage;
  stopReason: LlmStopReason | null;
}

/** Outcome of approving a parked tool call; `continuation` is set once the approval unblocked the model loop. */
export interface ToolApprovalResult {
  invocation: ToolInvocation;
  continuation: ChatTurnResult | null;
}

/** Server-sent events from the streaming endpoint, in order; `completed` is always last. */
export type ChatStreamEvent =
  | { event: "delta"; data: { text: string } }
  | { event: "tool_requested"; data: ToolInvocation }
  | { event: "tool_completed"; data: ToolInvocation }
  | { event: "notice"; data: { message: string } }
  | { event: "completed"; data: ChatTurnResult };

// ─── LLM credentials (bring your own key) ─────────────────────────────────────

export type LlmProvider = "Anthropic" | "OpenAiCompatible" | "AwsBedrock" | "GcpVertexAi" | "AzureFoundry";
export type LlmCredentialKind = "ApiKey" | "CloudIdentity";
export type LlmCredentialStatus = "Active" | "Invalid" | "Revoked";

export interface RegisterLlmCredentialRequest {
  name: string;
  provider: LlmProvider;
  model: string;
  /** Required for `ApiKey` credentials; omit for `CloudIdentity`. Sent once, never returned. */
  secret?: string;
  kind?: LlmCredentialKind;
  /** Required for `OpenAiCompatible` and `AzureFoundry`. Must be a public HTTPS host unless the operator allows private endpoints. */
  endpoint?: string;
  projectId?: string;
  nonSecretSettings?: Record<string, string>;
  isDefault?: boolean;
}

/** The redacted projection every read returns. Never contains the secret. */
export interface LlmCredentialSummary {
  id: string;
  workspaceId: string;
  projectId: string | null;
  name: string;
  provider: LlmProvider;
  kind: LlmCredentialKind;
  fingerprint: string;
  lastFour: string;
  endpoint: string | null;
  model: string;
  nonSecretSettings: Record<string, string>;
  isDefault: boolean;
  status: LlmCredentialStatus;
  createdAt: string;
  lastValidatedAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
}

export interface LlmCredentialValidationResult {
  credentialId: string;
  isValid: boolean;
  message: string;
  checkedAt: string;
}

export interface WorkspaceLlmPolicy {
  workspaceId: string;
  allowPlatformFallback: boolean;
  updatedAt: string;
  /** `null` means every registered tool; an empty array means none. */
  allowedTools: string[] | null;
}

export interface SetWorkspaceLlmPolicyRequest {
  allowPlatformFallback: boolean;
  /** Omit to leave the tool allowlist unchanged. */
  allowedTools?: string[];
}

// ─── Misc ─────────────────────────────────────────────────────────────────────

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

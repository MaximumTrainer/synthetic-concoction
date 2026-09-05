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

export type ComplianceProfileName = "Default" | "Healthcare" | "Finance";

export interface Workspace {
  id: string;
  accountId: string;
  name: string;
  createdAt: string;
  /** Fixed at creation. Governs generation defaults and what may be sent to a model provider. */
  complianceProfile: ComplianceProfileName;
}

export type ProjectStatus = "Active" | "Archived";

export interface Project {
  id: string;
  workspaceId: string;
  name: string;
  status: ProjectStatus;
  createdByUserId: string;
  createdAt: string;
  archivedAt: string | null;
}

export type ProjectDatabaseType = "Local" | "External";

export interface ProjectDatabase {
  id: string;
  projectId: string;
  name: string;
  type: ProjectDatabaseType;
  provider: string;
  status: string;
  connectionRefId: string | null;
  createdAt: string;
}

export type RunStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";

export interface DatasetRun {
  id: string;
  status: RunStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  seed: number;
  schemaSnapshotId: string | null;
  profileSnapshotId: string | null;
  requestedRowCounts: Record<string, number>;
  artifactChecksums: Record<string, string> | null;
  artifactPaths: string[] | null;
  validationIssueCount: number;
  failureReason: string | null;
  workspaceId: string | null;
}

export interface ApiKey {
  id: string;
  accountId: string;
  name: string;
  scopes: string[];
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  isRevoked: boolean;
  isExpired: boolean;
  isActive: boolean;
}

/** The one response that carries the plaintext secret. It is not returned again. */
export interface ApiKeyCreateResult {
  id: string;
  name: string;
  /** Plaintext secret — shown only once, at creation. */
  plaintextSecret: string;
  scopes: string[];
  expiresAt: string | null;
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
  /**
   * Registers the credential to you rather than to the workspace. A personal credential needs only workspace
   * membership, not admin — it is your key and your bill — and only you can read, rotate or use it. A workspace
   * admin sees that it exists and can revoke it for offboarding, but never reads it.
   */
  isPersonal?: boolean;
  /** Binds a personal credential to one chat session. Ignored unless `isPersonal`. */
  sessionId?: string;
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
  /** Set when the credential belongs to one member rather than to the workspace. */
  ownerUserId: string | null;
  /** Set when the credential is bound to a single chat session. */
  sessionId: string | null;
  isPersonal: boolean;
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
  /** Tokens per UTC day, or `null` for no cap. Over budget, chat returns a notice and calls no provider. */
  dailyTokenBudget: number | null;
  /** Tokens per UTC calendar month, or `null` for no cap. */
  monthlyTokenBudget: number | null;
  /** Whether members may attach their own credentials to this workspace. */
  allowPersonalCredentials: boolean;
  /**
   * Whether tool results carrying sampled row values or profiling aggregates may enter a prompt. Defaults to
   * false: schema metadata may leave the instance, the data itself may not until someone opts in.
   */
  allowSampledDataInPrompts: boolean;
}

export type LlmUsageGrouping = "Model" | "Credential" | "Day";

/**
 * One row of a usage rollup. `key` is a model name, a credential id (or `"platform"` for calls made on the
 * operator's own credential), or a `YYYY-MM-DD` UTC date, depending on the grouping requested.
 */
export interface LlmUsageBucket {
  key: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  calls: number;
  failedCalls: number;
}

export interface LlmUsageSummary {
  from: string;
  to: string;
  groupBy: LlmUsageGrouping;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  calls: number;
  failedCalls: number;
  buckets: LlmUsageBucket[];
}

export interface SetWorkspaceLlmPolicyRequest {
  allowPlatformFallback: boolean;
  /** Omit to leave the tool allowlist unchanged. */
  allowedTools?: string[];
  /**
   * Omit to leave unchanged. Setting it to `true` on a `Healthcare` or `Finance` workspace is **refused** with
   * HTTP 409 and leaves the policy untouched — the answer is fixed by the compliance profile, not per workspace.
   */
  allowSampledDataInPrompts?: boolean;
  /** Tokens per UTC day. Omit to leave unchanged; pass `-1` to clear the cap. */
  dailyTokenBudget?: number;
  /** Tokens per UTC calendar month. Omit to leave unchanged; pass `-1` to clear the cap. */
  monthlyTokenBudget?: number;
  /**
   * Whether members may attach their own credentials. Omit to leave unchanged. Setting it to `false` both blocks
   * new personal credentials and makes existing ones unresolvable immediately.
   */
  allowPersonalCredentials?: boolean;
}

// ─── Misc ─────────────────────────────────────────────────────────────────────

export type WorkflowStatus = "Active" | "Disabled" | "Archived";

export interface Workflow {
  id: string;
  workspaceId: string;
  name: string;
  version: number;
  status: WorkflowStatus;
  createdAt: string;
}

export interface WorkflowStepInput {
  stepOrder: number;
  stepType: string;
  configuration?: string | null;
}

export type WorkflowRunStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";

export interface WorkflowRun {
  id: string;
  workflowId: string;
  status: WorkflowRunStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
}

export interface WorkflowStepRun {
  id: string;
  workflowRunId: string;
  stepId: string;
  stepOrder: number;
  status: WorkflowRunStatus;
  retryCount: number;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
}

export interface InstructionVersion {
  id: string;
  workspaceId: string;
  version: number;
  content: string;
  createdByUserId: string;
  createdAt: string;
  projectId: string | null;
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

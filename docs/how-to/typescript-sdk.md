# TypeScript SDK (`@fabricate/client`)

The `@fabricate/client` npm package provides a typed client for the Fabricate REST API. It ships as CJS + ESM + TypeScript declaration files (`.d.ts`).

## Installation

```bash
npm install @fabricate/client
```

Or from the local source:

```bash
cd sdk/typescript
npm install
npm run build
```

The built package is in `sdk/typescript/dist/`.

## Quick Start

```typescript
import { FabricateClient, FabricateError } from "@fabricate/client";

const client = new FabricateClient({
  baseUrl: "http://localhost:5000",
  apiKey: "cnc_yoursecrethere",
});

// Create an account
const account = await client.createAccount("Acme Corp");
console.log(account.id);

// Create a workspace
const workspace = await client.createWorkspace(account.id, "Production Clone");

// Create a project
const project = await client.createProject(workspace.id, "Orders Dataset");
```

## Client Constructor Options

```typescript
interface FabricateClientOptions {
  /** Base URL of the Fabricate API */
  baseUrl: string;
  /** API key (sent as X-Api-Key header) */
  apiKey: string;
  /** Optional custom fetch implementation — defaults to globalThis.fetch */
  fetch?: typeof globalThis.fetch;
}
```

## Methods

### Accounts

#### `createAccount(name: string): Promise<Account>`

```typescript
const account = await client.createAccount("Acme Corp");
// { id: "3fa85f64-...", name: "Acme Corp", createdAt: "2024-06-01T10:00:00Z" }
```

#### `getAccount(accountId: string): Promise<Account>`

```typescript
const account = await client.getAccount("3fa85f64-...");
```

#### `listMembers(accountId: string): Promise<AccountMembership[]>`

```typescript
const members = await client.listMembers("3fa85f64-...");
// [{ accountId: "...", userId: "...", role: "Owner", joinedAt: "..." }]
```

#### `inviteUser(accountId: string, email: string, expiryHours?: number): Promise<void>`

```typescript
await client.inviteUser("3fa85f64-...", "bob@example.com", 48);
```

---

### Workspaces

#### `createWorkspace(accountId: string, name: string): Promise<Workspace>`

```typescript
const workspace = await client.createWorkspace(account.id, "Dev Sandbox");
```

#### `getWorkspace(workspaceId: string): Promise<Workspace>`

```typescript
const workspace = await client.getWorkspace("9a3b2c1d-...");
```

---

### Projects

#### `createProject(workspaceId: string, name: string): Promise<Project>`

```typescript
const project = await client.createProject(workspace.id, "Orders Dataset");
```

#### `listProjects(workspaceId: string): Promise<Project[]>`

```typescript
const projects = await client.listProjects(workspace.id);
```

#### `getProject(projectId: string): Promise<Project>`

```typescript
const project = await client.getProject("proj-001");
```

#### `archiveProject(projectId: string): Promise<void>`

```typescript
await client.archiveProject("proj-001");
```

---

### Dataset Runs

#### `listRuns(projectId: string, page?: number, pageSize?: number): Promise<PaginatedResult<DatasetRun>>`

```typescript
const page = await client.listRuns(project.id, 1, 20);
console.log(page.items.length, page.totalCount);
```

#### `getRun(runId: string): Promise<DatasetRun>`

```typescript
const run = await client.getRun("run-abc123");
console.log(run.status); // "Queued" | "Running" | "Completed" | "Failed" | "Cancelled"
```

#### `cancelRun(runId: string): Promise<void>`

```typescript
await client.cancelRun("run-abc123");
```

#### `pollRun(runId: string, options?: { intervalMs?: number; timeoutMs?: number }): Promise<DatasetRun>`

Polls a run at `intervalMs` (default 2 000ms) until it reaches a terminal state (`Completed`, `Failed`, `Cancelled`).

- Throws `FabricateError` if the run fails or is cancelled.
- Throws `FabricateError` if polling times out (default `timeoutMs = 120 000ms`).

```typescript
const workflowResult = await client.runWorkflow(workflowId);
const completedRun = await client.pollRun(workflowResult.runId, {
  intervalMs: 3000,
  timeoutMs: 300_000,
});
console.log(`Run completed at ${completedRun.completedAt}`);
```

---

### Workflows

#### `createWorkflow(workspaceId: string, name: string, steps: unknown[]): Promise<Workflow>`

```typescript
const workflow = await client.createWorkflow(workspace.id, "Nightly Refresh", [
  { type: "generate", rows: 500 },
  { type: "export", format: "sql" },
]);
```

#### `runWorkflow(workflowId: string): Promise<{ runId: string }>`

```typescript
const { runId } = await client.runWorkflow(workflow.id);
const run = await client.pollRun(runId);
```

---

### Chat

Chat is answered by the LLM the workspace is configured to use — a credential the workspace registered
([bring your own key](byok-llm-credentials.md)) or, where allowed, the operator's platform credential. All chat
methods take the `workspaceId` first.

#### `createChatSession(workspaceId, name, { projectId?, mode? }): Promise<ChatSession>`

`mode` is `"Guided"` (default), `"Autonomous"` or `"ReviewRequired"` (tool calls are parked until approved).

```typescript
const session = await client.createChatSession(workspace.id, "Schema exploration", { mode: "ReviewRequired" });
```

#### `sendMessage(workspaceId, sessionId, content): Promise<ChatTurnResult>`

Runs the whole turn — model call, tool loop — and returns your message, the reply, every tool invocation, token
usage and a stop reason. On a refusal, provider failure or missing credential, `assistantMessage` is a `System`
notice and `stopReason` is `Refusal` / `Error` / `null`; no exception is thrown.

```typescript
const turn = await client.sendMessage(workspace.id, session.id, "Show me the schema.");
console.log(turn.assistantMessage?.content, turn.usage.totalTokens);
for (const call of turn.toolInvocations) console.log(call.toolName, call.status);
```

#### `streamMessage(workspaceId, sessionId, content): AsyncGenerator<ChatStreamEvent>`

The same turn as server-sent events: `delta`, `tool_requested`, `tool_completed`, `notice`, then a terminal
`completed` carrying the full `ChatTurnResult`.

```typescript
for await (const evt of client.streamMessage(workspace.id, session.id, "Generate 50 rows per table.")) {
  if (evt.event === "delta") process.stdout.write(evt.data.text);
  if (evt.event === "completed") console.log("\nstop:", evt.data.stopReason);
}
```

#### `listToolInvocations(workspaceId, sessionId): Promise<ToolInvocation[]>`

#### `approveToolInvocation(workspaceId, sessionId, invocationId): Promise<ToolApprovalResult>`

Runs a `Pending` call from a `ReviewRequired` session (Editor role or above). Once every parked call is decided the
model loop resumes and the resulting turn is returned as `continuation`.

```typescript
const pending = turn.toolInvocations.filter((i) => i.status === "Pending");
for (const call of pending) {
  const { invocation, continuation } = await client.approveToolInvocation(workspace.id, session.id, call.id);
  if (continuation) console.log(continuation.assistantMessage?.content);
}
```

#### `getChatHistory(workspaceId, sessionId, pageSize?): Promise<ChatMessage[]>`

#### `setChatMode(workspaceId, sessionId, mode): Promise<ChatSession>`

#### `archiveChatSession(workspaceId, sessionId): Promise<ChatSession>`

---

### LLM credentials (bring your own key)

Every response is a redacted `LlmCredentialSummary` (fingerprint and last four characters — never the secret).
Register, rotate, revoke and policy changes need the workspace Admin role; list and validate need any role.

```typescript
const credential = await client.registerLlmCredential(workspace.id, {
  name: "team-anthropic",
  provider: "Anthropic",
  model: "claude-opus-5",
  secret: process.env.ANTHROPIC_API_KEY!,
  isDefault: true,
});

const check = await client.validateLlmCredential(workspace.id, credential.id);   // minimal provider probe
await client.rotateLlmCredential(workspace.id, credential.id, "sk-ant-new…");
const all = await client.listLlmCredentials(workspace.id);
await client.revokeLlmCredential(workspace.id, credential.id);                   // soft; excluded immediately

// Per-workspace policy: platform-credential fallback and which tools the model may be offered.
await client.setWorkspaceLlmPolicy(workspace.id, { allowPlatformFallback: false, allowedTools: ["discover_schema"] });
```

An OpenAI-compatible endpoint (OpenAI, Azure OpenAI, vLLM, Ollama, gateways) uses `provider: "OpenAiCompatible"`
with a public HTTPS `endpoint`; Bedrock and Vertex use `kind: "CloudIdentity"` with `nonSecretSettings`
(`region` / `projectId`, `location`) and no secret.

---

### API Keys

#### `createApiKey(accountId, displayName, scopes, expiresAt?): Promise<ApiKeyCreateResult>`

```typescript
const result = await client.createApiKey(
  account.id,
  "ci-pipeline",
  ["workspace:read", "workspace:write"],
  "2025-01-01T00:00:00Z"  // ISO 8601 expiry; omit for no expiry
);
console.log(result.secret); // cnc_abc123... — shown once only
```

#### `listApiKeys(accountId: string): Promise<ApiKey[]>`

```typescript
const keys = await client.listApiKeys(account.id);
```

#### `revokeApiKey(keyId: string): Promise<void>`

```typescript
await client.revokeApiKey("3fa85f64-...");
```

---

## Error Handling

All HTTP errors throw `FabricateError`:

```typescript
try {
  const account = await client.getAccount("non-existent-id");
} catch (err) {
  if (err instanceof FabricateError) {
    console.error(`HTTP ${err.status}: ${err.message}`);
    console.error(err.detail); // Problem Details "detail" field if available
  }
}
```

### FabricateError Properties

| Property | Type | Description |
|---|---|---|
| `message` | `string` | `"HTTP 404 Not Found"` |
| `status` | `number` | HTTP status code (0 for timeout/cancelled errors) |
| `detail` | `string \| undefined` | Problem Details `detail` or `title` field |

---

## Type Definitions

The SDK exports all types from `@fabricate/client`:

```typescript
import type {
  Account,
  AccountMembership,
  ApiKey,
  ApiKeyCreateResult,
  ChatMessage,
  ChatMode,
  ChatSession,
  ChatStreamEvent,
  ChatTurnResult,
  FabricateClientOptions,
  DatasetRun,
  LlmCredentialSummary,
  LlmCredentialValidationResult,
  LlmProvider,
  PaginatedResult,
  Project,
  RegisterLlmCredentialRequest,
  TokenUsage,
  ToolApprovalResult,
  ToolInvocation,
  Workspace,
  WorkspaceLlmPolicy,
  Workflow,
  ComplianceProfile,
} from "@fabricate/client";
```

---

## Building from Source

```bash
cd sdk/typescript
npm install
npm run build
```

Build output is in `dist/`. The package uses **tsup** to produce:

- `dist/index.cjs` — CommonJS
- `dist/index.js` — ES Module
- `dist/index.d.ts` — TypeScript declarations

## Custom Fetch (Node.js < 18 / Testing)

```typescript
import fetch from "node-fetch";

const client = new FabricateClient({
  baseUrl: "http://localhost:5000",
  apiKey: "cnc_...",
  fetch: fetch as unknown as typeof globalThis.fetch,
});
```

For testing, pass a mock `fetch` to intercept requests:

```typescript
const mockFetch = vi.fn().mockResolvedValue(
  new Response(JSON.stringify({ id: "acc-1", name: "Test" }), { status: 200 })
);

const client = new FabricateClient({
  baseUrl: "http://localhost:5000",
  apiKey: "cnc_test",
  fetch: mockFetch,
});
```

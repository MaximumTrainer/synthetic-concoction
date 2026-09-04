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
  PaginatedResult,
  Project,
  RegisterLlmCredentialRequest,
  SetWorkspaceLlmPolicyRequest,
  ToolApprovalResult,
  ToolInvocation,
  Workspace,
  WorkspaceLlmPolicy,
  Workflow,
} from "./types.js";

export class FabricateError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly detail?: string
  ) {
    super(message);
    this.name = "FabricateError";
  }
}

export class FabricateClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;
  private readonly fetchFn: typeof globalThis.fetch;

  constructor(options: FabricateClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, "");
    this.apiKey = options.apiKey;
    this.fetchFn = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  // ─── Accounts ────────────────────────────────────────────────────────────────

  async createAccount(name: string): Promise<Account> {
    return this.post<Account>("/accounts", { name });
  }

  async getAccount(accountId: string): Promise<Account> {
    return this.get<Account>(`/accounts/${accountId}`);
  }

  async listMembers(accountId: string): Promise<AccountMembership[]> {
    return this.get<AccountMembership[]>(`/accounts/${accountId}/members`);
  }

  async inviteUser(
    accountId: string,
    email: string,
    expiresInHours = 72
  ): Promise<{ invitationId: string; token: string }> {
    return this.post<{ invitationId: string; token: string }>(
      `/accounts/${accountId}/invitations`,
      { email, expiresInHours }
    );
  }

  // ─── Workspaces ──────────────────────────────────────────────────────────────

  async createWorkspace(
    accountId: string,
    name: string
  ): Promise<Workspace> {
    return this.post<Workspace>("/workspaces", { accountId, name });
  }

  async getWorkspace(workspaceId: string): Promise<Workspace> {
    return this.get<Workspace>(`/workspaces/${workspaceId}`);
  }

  // ─── Projects ────────────────────────────────────────────────────────────────

  async createProject(
    workspaceId: string,
    name: string
  ): Promise<Project> {
    return this.post<Project>("/projects", { workspaceId, name });
  }

  async listProjects(workspaceId: string): Promise<Project[]> {
    return this.get<Project[]>(`/projects?workspaceId=${workspaceId}`);
  }

  async getProject(projectId: string): Promise<Project> {
    return this.get<Project>(`/projects/${projectId}`);
  }

  async archiveProject(projectId: string): Promise<void> {
    await this.post(`/projects/${projectId}/archive`, {});
  }

  // ─── Runs ────────────────────────────────────────────────────────────────────

  async listRuns(
    page = 1,
    pageSize = 20
  ): Promise<PaginatedResult<DatasetRun>> {
    return this.get<PaginatedResult<DatasetRun>>(
      `/runs?page=${page}&pageSize=${pageSize}`
    );
  }

  async getRun(runId: string): Promise<DatasetRun> {
    return this.get<DatasetRun>(`/runs/${runId}`);
  }

  async cancelRun(runId: string): Promise<void> {
    await this.post(`/runs/${runId}/cancel`, {});
  }

  /**
   * Polls a run until it reaches a terminal state.
   * Throws FabricateError if the run fails or is cancelled.
   */
  async pollRun(
    runId: string,
    intervalMs = 2000,
    timeoutMs = 300_000
  ): Promise<DatasetRun> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const run = await this.getRun(runId);
      if (run.status === "Completed") return run;
      if (run.status === "Failed") throw new FabricateError(`Run ${runId} failed`, 0);
      if (run.status === "Cancelled") throw new FabricateError(`Run ${runId} was cancelled`, 0);
      await sleep(intervalMs);
    }

    throw new FabricateError(`Timed out polling run ${runId}`, 0);
  }

  // ─── Workflows ───────────────────────────────────────────────────────────────

  async createWorkflow(
    workspaceId: string,
    name: string,
    steps: unknown[]
  ): Promise<Workflow> {
    return this.post<Workflow>("/workflows", { workspaceId, name, steps });
  }

  async runWorkflow(workflowId: string): Promise<{ runId: string }> {
    return this.post<{ runId: string }>(`/workflows/${workflowId}/runs`, {});
  }

  // ─── Chat ────────────────────────────────────────────────────────────────────

  async createChatSession(
    workspaceId: string,
    name: string,
    options: { projectId?: string; mode?: ChatMode } = {}
  ): Promise<ChatSession> {
    return this.post<ChatSession>(`/workspaces/${workspaceId}/chat/sessions`, {
      name,
      projectId: options.projectId ?? null,
      mode: options.mode ?? "Guided",
    });
  }

  /** Runs the whole turn (model call and tool loop) and returns it once complete. */
  async sendMessage(
    workspaceId: string,
    sessionId: string,
    content: string
  ): Promise<ChatTurnResult> {
    return this.post<ChatTurnResult>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/messages`,
      { content }
    );
  }

  /**
   * Same turn as `sendMessage`, yielded incrementally as server-sent events. The last event is always
   * `completed`, carrying the full `ChatTurnResult`.
   */
  async *streamMessage(
    workspaceId: string,
    sessionId: string,
    content: string
  ): AsyncGenerator<ChatStreamEvent, void, undefined> {
    const res = await this.fetchFn(
      `${this.baseUrl}/workspaces/${workspaceId}/chat/sessions/${sessionId}/messages/stream`,
      {
        method: "POST",
        headers: { ...this.headers(), "Content-Type": "application/json", Accept: "text/event-stream" },
        body: JSON.stringify({ content }),
      }
    );

    if (!res.ok) {
      await this.handleResponse<unknown>(res);
      return;
    }
    if (!res.body) {
      throw new FabricateError("Streaming response has no body", res.status);
    }

    for await (const event of parseSse(res.body)) {
      yield event as ChatStreamEvent;
      if (event.event === "completed") return;
    }
  }

  async getChatHistory(
    workspaceId: string,
    sessionId: string,
    pageSize = 50
  ): Promise<ChatMessage[]> {
    return this.get<ChatMessage[]>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/messages?pageSize=${pageSize}`
    );
  }

  async listToolInvocations(
    workspaceId: string,
    sessionId: string
  ): Promise<ToolInvocation[]> {
    return this.get<ToolInvocation[]>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/tool-invocations`
    );
  }

  /** Runs a `Pending` tool call from a `ReviewRequired` session. Requires the workspace Editor role or above. */
  async approveToolInvocation(
    workspaceId: string,
    sessionId: string,
    invocationId: string
  ): Promise<ToolApprovalResult> {
    return this.post<ToolApprovalResult>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/tool-invocations/${invocationId}/approve`,
      {}
    );
  }

  async setChatMode(
    workspaceId: string,
    sessionId: string,
    mode: ChatMode
  ): Promise<ChatSession> {
    return this.patch<ChatSession>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/mode`,
      { mode }
    );
  }

  async archiveChatSession(workspaceId: string, sessionId: string): Promise<ChatSession> {
    return this.post<ChatSession>(
      `/workspaces/${workspaceId}/chat/sessions/${sessionId}/archive`,
      {}
    );
  }

  // ─── LLM credentials (bring your own key) ────────────────────────────────────

  /** Registers a workspace LLM credential. The secret is sent once; every response is a redacted summary. */
  async registerLlmCredential(
    workspaceId: string,
    request: RegisterLlmCredentialRequest
  ): Promise<LlmCredentialSummary> {
    return this.post<LlmCredentialSummary>(
      `/workspaces/${workspaceId}/llm-credentials`,
      request
    );
  }

  async listLlmCredentials(workspaceId: string): Promise<LlmCredentialSummary[]> {
    return this.get<LlmCredentialSummary[]>(`/workspaces/${workspaceId}/llm-credentials`);
  }

  async rotateLlmCredential(
    workspaceId: string,
    credentialId: string,
    secret: string
  ): Promise<LlmCredentialSummary> {
    return this.post<LlmCredentialSummary>(
      `/workspaces/${workspaceId}/llm-credentials/${credentialId}/rotate`,
      { secret }
    );
  }

  /** Makes a minimal provider call to prove the credential works. Rate-limited server-side. */
  async validateLlmCredential(
    workspaceId: string,
    credentialId: string
  ): Promise<LlmCredentialValidationResult> {
    return this.post<LlmCredentialValidationResult>(
      `/workspaces/${workspaceId}/llm-credentials/${credentialId}/validate`,
      {}
    );
  }

  async revokeLlmCredential(workspaceId: string, credentialId: string): Promise<void> {
    await this.delete(`/workspaces/${workspaceId}/llm-credentials/${credentialId}`);
  }

  async getWorkspaceLlmPolicy(workspaceId: string): Promise<WorkspaceLlmPolicy> {
    return this.get<WorkspaceLlmPolicy>(`/workspaces/${workspaceId}/llm-credentials/policy`);
  }

  async setWorkspaceLlmPolicy(
    workspaceId: string,
    request: SetWorkspaceLlmPolicyRequest
  ): Promise<WorkspaceLlmPolicy> {
    return this.put<WorkspaceLlmPolicy>(
      `/workspaces/${workspaceId}/llm-credentials/policy`,
      request
    );
  }

  // ─── API Keys ─────────────────────────────────────────────────────────────────

  async createApiKey(
    accountId: string,
    displayName: string,
    scopes: string[],
    expiresAt?: string
  ): Promise<ApiKeyCreateResult> {
    return this.post<ApiKeyCreateResult>("/api-keys", {
      accountId,
      displayName,
      scopes,
      expiresAt,
    });
  }

  async listApiKeys(accountId: string): Promise<ApiKey[]> {
    return this.get<ApiKey[]>(`/api-keys?accountId=${accountId}`);
  }

  async revokeApiKey(keyId: string): Promise<void> {
    await this.post(`/api-keys/${keyId}/revoke`, {});
  }

  // ─── HTTP helpers ─────────────────────────────────────────────────────────────

  private async get<T>(path: string): Promise<T> {
    const res = await this.fetchFn(`${this.baseUrl}${path}`, {
      method: "GET",
      headers: this.headers(),
    });
    return this.handleResponse<T>(res);
  }

  private post<T>(path: string, body: unknown): Promise<T> {
    return this.send<T>("POST", path, body);
  }

  private put<T>(path: string, body: unknown): Promise<T> {
    return this.send<T>("PUT", path, body);
  }

  private patch<T>(path: string, body: unknown): Promise<T> {
    return this.send<T>("PATCH", path, body);
  }

  private async delete(path: string): Promise<void> {
    const res = await this.fetchFn(`${this.baseUrl}${path}`, {
      method: "DELETE",
      headers: this.headers(),
    });
    await this.handleResponse<unknown>(res);
  }

  private async send<T>(method: string, path: string, body: unknown): Promise<T> {
    const res = await this.fetchFn(`${this.baseUrl}${path}`, {
      method,
      headers: { ...this.headers(), "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    return this.handleResponse<T>(res);
  }

  private headers(): Record<string, string> {
    return { "X-Api-Key": this.apiKey };
  }

  private async handleResponse<T>(res: Response): Promise<T> {
    if (res.status === 204) return undefined as unknown as T;

    const text = await res.text();

    if (!res.ok) {
      let detail: string | undefined;
      try {
        const problem = JSON.parse(text) as { detail?: string; title?: string };
        detail = problem.detail ?? problem.title;
      } catch {
        detail = text;
      }
      throw new FabricateError(
        `HTTP ${res.status} ${res.statusText}`,
        res.status,
        detail
      );
    }

    if (!text) return undefined as unknown as T;
    return JSON.parse(text) as T;
  }
}

/** Minimal SSE parser: yields one `{ event, data }` per blank-line-terminated block; `data` is parsed as JSON. */
export async function* parseSse(
  body: ReadableStream<Uint8Array>
): AsyncGenerator<{ event: string; data: unknown }, void, undefined> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      // Normalise CRLF so block boundaries are always "\n\n". A trailing "\r" cut off by the chunk boundary is
      // completed by the next chunk's "\n" and normalised on the following pass.
      buffer = (buffer + decoder.decode(value, { stream: true })).replace(/\r\n/g, "\n");

      let boundary: number;
      while ((boundary = buffer.indexOf("\n\n")) >= 0) {
        const block = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        const parsed = parseBlock(block);
        if (parsed) yield parsed;
      }
    }

    const tail = parseBlock(buffer);
    if (tail) yield tail;
  } finally {
    reader.releaseLock();
  }
}

function parseBlock(block: string): { event: string; data: unknown } | null {
  let event = "message";
  const dataLines: string[] = [];
  for (const rawLine of block.split("\n")) {
    const line = rawLine.replace(/\r$/, "");
    if (line.startsWith("event:")) event = line.slice("event:".length).trim();
    else if (line.startsWith("data:")) dataLines.push(line.slice("data:".length).trimStart());
  }
  if (dataLines.length === 0) return null;
  const raw = dataLines.join("\n");
  try {
    return { event, data: JSON.parse(raw) };
  } catch {
    return { event, data: raw };
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

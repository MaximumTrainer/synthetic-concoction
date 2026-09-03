import type {
  Account,
  AccountMembership,
  ApiKey,
  ApiKeyCreateResult,
  ChatMessage,
  ChatSession,
  FabricateClientOptions,
  DatasetRun,
  PaginatedResult,
  Project,
  Workspace,
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

  // ─── Accounts ───────────────────────────────────────────────────────────────

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
    expiryHours = 48
  ): Promise<void> {
    await this.post(`/accounts/${accountId}/invitations`, {
      email,
      expiryHours,
    });
  }

  // ─── Workspaces ─────────────────────────────────────────────────────────────

  async createWorkspace(
    accountId: string,
    name: string
  ): Promise<Workspace> {
    return this.post<Workspace>("/workspaces", { accountId, name });
  }

  async getWorkspace(workspaceId: string): Promise<Workspace> {
    return this.get<Workspace>(`/workspaces/${workspaceId}`);
  }

  // ─── Projects ───────────────────────────────────────────────────────────────

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

  // ─── Dataset Runs ────────────────────────────────────────────────────────────

  async listRuns(
    projectId: string,
    page = 1,
    pageSize = 20
  ): Promise<PaginatedResult<DatasetRun>> {
    return this.get<PaginatedResult<DatasetRun>>(
      `/runs?projectId=${projectId}&page=${page}&pageSize=${pageSize}`
    );
  }

  async getRun(runId: string): Promise<DatasetRun> {
    return this.get<DatasetRun>(`/runs/${runId}`);
  }

  async cancelRun(runId: string): Promise<void> {
    await this.post(`/runs/${runId}/cancel`, {});
  }

  /**
   * Poll a run until it reaches a terminal state (Completed, Failed, Cancelled).
   * Rejects if the run fails or polling times out.
   */
  async pollRun(
    runId: string,
    options: { intervalMs?: number; timeoutMs?: number } = {}
  ): Promise<DatasetRun> {
    const { intervalMs = 2000, timeoutMs = 120_000 } = options;
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
    title: string
  ): Promise<ChatSession> {
    return this.post<ChatSession>("/chat/sessions", { workspaceId, title });
  }

  async sendMessage(
    sessionId: string,
    content: string
  ): Promise<ChatMessage> {
    return this.post<ChatMessage>(`/chat/sessions/${sessionId}/messages`, {
      content,
    });
  }

  async getChatHistory(sessionId: string): Promise<ChatMessage[]> {
    return this.get<ChatMessage[]>(`/chat/sessions/${sessionId}/messages`);
  }

  async archiveChatSession(sessionId: string): Promise<void> {
    await this.post(`/chat/sessions/${sessionId}/archive`, {});
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

  private async post<T>(path: string, body: unknown): Promise<T> {
    const res = await this.fetchFn(`${this.baseUrl}${path}`, {
      method: "POST",
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

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

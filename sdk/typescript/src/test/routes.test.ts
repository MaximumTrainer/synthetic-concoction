import { test } from "node:test";
import assert from "node:assert/strict";
import { FabricateClient } from "../client.js";
import type { ApiKey, DatasetRun, Project } from "../types.js";

// #73: every non-chat method used to call a route the API does not expose — `/projects`, `/workflows`,
// `/api-keys` — so the SDK was unusable outside chat. These pin the method, verb and payload of every one.

type Recorded = { url: string; method: string; body: unknown };

function recorder(respond: (req: Recorded) => unknown = () => ({})) {
  const calls: Recorded[] = [];
  const fetch = (async (input: string | URL | Request, init?: RequestInit) => {
    const req = {
      url: String(input),
      method: init?.method ?? "GET",
      body: init?.body ? JSON.parse(init.body as string) : undefined,
    };
    calls.push(req);
    return new Response(JSON.stringify(respond(req)), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  }) as typeof globalThis.fetch;

  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });
  const trace = () => calls.map((c) => `${c.method} ${c.url.replace("https://api.test", "")}`);
  return { client, calls, trace };
}

test("project methods are workspace-scoped", async () => {
  const { client, calls, trace } = recorder();

  await client.createProject("ws1", "Analytics");
  await client.listProjects("ws1");
  await client.getProject("ws1", "p1");
  await client.renameProject("ws1", "p1", "Renamed");
  await client.archiveProject("ws1", "p1");
  await client.listProjectDatabases("ws1", "p1");
  await client.addProjectDatabase("ws1", "p1", { name: "warehouse", type: "External", provider: "postgres" });
  await client.saveProjectInstruction("ws1", "p1", "Prefer UK addresses.");
  await client.getProjectInstruction("ws1", "p1");

  assert.deepEqual(trace(), [
    "POST /workspaces/ws1/projects",
    "GET /workspaces/ws1/projects",
    "GET /workspaces/ws1/projects/p1",
    "PATCH /workspaces/ws1/projects/p1/name",
    "POST /workspaces/ws1/projects/p1/archive",
    "GET /workspaces/ws1/projects/p1/databases",
    "POST /workspaces/ws1/projects/p1/databases",
    "POST /workspaces/ws1/projects/p1/instructions",
    "GET /workspaces/ws1/projects/p1/instructions",
  ]);

  // The workspace comes from the route, so it must not be repeated in the body.
  assert.deepEqual(calls[0].body, { name: "Analytics" });
  assert.deepEqual(calls[6].body, {
    name: "warehouse",
    type: "External",
    provider: "postgres",
    connectionRefId: null,
  });
  assert.deepEqual(calls[7].body, { content: "Prefer UK addresses." });
});

test("archiveProject returns the archived project rather than discarding it", async () => {
  const archived: Project = {
    id: "p1",
    workspaceId: "ws1",
    name: "Analytics",
    status: "Archived",
    createdByUserId: "u1",
    createdAt: "2026-09-03T00:00:00Z",
    archivedAt: "2026-09-04T00:00:00Z",
  };
  const { client } = recorder(() => archived);

  const result = await client.archiveProject("ws1", "p1");

  assert.equal(result.status, "Archived");
  assert.equal(result.archivedAt, "2026-09-04T00:00:00Z");
});

test("workflow methods are workspace-scoped and return run records", async () => {
  const { client, calls, trace } = recorder((req) =>
    req.url.endsWith("/runs") && req.method === "POST"
      ? { id: "r1", workflowId: "wf1", status: "Queued", createdAt: "2026-09-03T00:00:00Z" }
      : {}
  );

  await client.createWorkflow("ws1", "Nightly", [
    { stepOrder: 1, stepType: "generate", configuration: null },
  ]);
  const run = await client.runWorkflow("ws1", "wf1");
  await client.getWorkflowRun("ws1", "wf1", "r1");
  await client.getWorkflowStepRuns("ws1", "wf1", "r1");
  await client.disableWorkflow("ws1", "wf1");

  assert.deepEqual(trace(), [
    "POST /workspaces/ws1/workflows",
    "POST /workspaces/ws1/workflows/wf1/runs",
    "GET /workspaces/ws1/workflows/wf1/runs/r1",
    "GET /workspaces/ws1/workflows/wf1/runs/r1/steps",
    "POST /workspaces/ws1/workflows/wf1/disable",
  ]);

  assert.deepEqual(calls[0].body, {
    name: "Nightly",
    steps: [{ stepOrder: 1, stepType: "generate", configuration: null }],
  });

  // runWorkflow returns the whole run, not a bare { runId }.
  assert.equal(run.id, "r1");
  assert.equal(run.status, "Queued");
});

test("API keys live under the account and revoke is a DELETE", async () => {
  const key: ApiKey = {
    id: "k1",
    accountId: "a1",
    name: "ci",
    scopes: ["read", "write"],
    createdAt: "2026-09-03T00:00:00Z",
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: "2026-09-04T00:00:00Z",
    isRevoked: true,
    isExpired: false,
    isActive: false,
  };
  const { client, calls, trace } = recorder((req) =>
    req.method === "POST"
      ? { id: "k1", name: "ci", plaintextSecret: "cnc_plaintext", scopes: ["read", "write"], expiresAt: null }
      : key
  );

  const created = await client.createApiKey("a1", "ci", ["read", "write"], { days: 30 });
  await client.listApiKeys("a1");
  const revoked = await client.revokeApiKey("a1", "k1");

  assert.deepEqual(trace(), [
    "POST /accounts/a1/api-keys",
    "GET /accounts/a1/api-keys",
    "DELETE /accounts/a1/api-keys/k1",
  ]);

  // The API binds a TimeSpan lifetime, not an instant.
  assert.deepEqual(calls[0].body, { name: "ci", scopes: ["read", "write"], expiry: "30.00:00:00" });
  assert.equal(created.plaintextSecret, "cnc_plaintext", "the plaintext is returned flat, not nested under `key`");
  assert.equal(revoked.isRevoked, true);
});

test("createApiKey omits the expiry for a key that never expires", async () => {
  const { client, calls } = recorder();

  await client.createApiKey("a1", "forever", ["read"]);

  assert.deepEqual(calls[0].body, { name: "forever", scopes: ["read"], expiry: null });
});

test("listRuns returns a bare array and cancelRun returns the run", async () => {
  const run: DatasetRun = {
    id: "r1",
    status: "Cancelled",
    createdAt: "2026-09-03T00:00:00Z",
    startedAt: null,
    completedAt: null,
    seed: 5150,
    schemaSnapshotId: null,
    profileSnapshotId: null,
    requestedRowCounts: { "main.users": 100 },
    artifactChecksums: null,
    artifactPaths: null,
    validationIssueCount: 0,
    failureReason: null,
    workspaceId: null,
  };
  const { client, trace } = recorder((req) => (req.method === "GET" && req.url.includes("?") ? [run] : run));

  const runs = await client.listRuns(2, 50);
  const cancelled = await client.cancelRun("r1");

  assert.deepEqual(trace(), ["GET /runs?page=2&pageSize=50", "POST /runs/r1/cancel"]);
  assert.equal(runs.length, 1);
  assert.equal(runs[0].seed, 5150);
  assert.equal(cancelled.status, "Cancelled");
});

test("account and workspace methods keep their routes", async () => {
  const { client, calls, trace } = recorder();

  await client.createAccount("Acme");
  await client.getAccount("a1");
  await client.listMembers("a1");
  await client.inviteUser("a1", "person@example.com");
  await client.createWorkspace("a1", "Platform");
  await client.getWorkspace("ws1");

  assert.deepEqual(trace(), [
    "POST /accounts",
    "GET /accounts/a1",
    "GET /accounts/a1/members",
    "POST /accounts/a1/invitations",
    "POST /workspaces",
    "GET /workspaces/ws1",
  ]);
  assert.deepEqual(calls[3].body, { email: "person@example.com", expiresInHours: 72 });
  assert.deepEqual(calls[4].body, {
    accountId: "a1",
    name: "Platform",
    complianceProfile: "Default",
  }, "the compliance profile is fixed at creation, so it is always sent");
});

test("pollRun stops on a terminal status", async () => {
  let polls = 0;
  const { client } = recorder(() => {
    polls++;
    return { id: "r1", status: polls < 2 ? "Running" : "Completed" };
  });

  const run = await client.pollRun("r1", 1, 5_000);

  assert.equal(run.status, "Completed");
  assert.equal(polls, 2);
});

test("LLM usage methods hit the workspace and account rollups", async () => {
  const { client, trace } = recorder(() => ({ buckets: [], totalTokens: 0 }));

  await client.getWorkspaceLlmUsage("ws1");
  await client.getWorkspaceLlmUsage("ws1", { from: "2026-09-01T00:00:00Z", groupBy: "Day" });
  await client.getAccountLlmUsage("a1", { groupBy: "Credential" });

  assert.deepEqual(trace(), [
    "GET /workspaces/ws1/llm-usage",
    "GET /workspaces/ws1/llm-usage?from=2026-09-01T00%3A00%3A00Z&groupBy=Day",
    "GET /accounts/a1/llm-usage?groupBy=Credential",
  ]);
});

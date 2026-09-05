import { test } from "node:test";
import assert from "node:assert/strict";
import { FabricateClient, FabricateError, parseSse } from "../client.js";
import type { ChatStreamEvent, ChatTurnResult, LlmCredentialSummary } from "../types.js";

type Recorded = { url: string; method: string; headers: Record<string, string>; body: unknown };

function fakeFetch(
  respond: (req: Recorded) => Response | Promise<Response>
): { fetch: typeof globalThis.fetch; calls: Recorded[] } {
  const calls: Recorded[] = [];
  const fetch = (async (input: string | URL | Request, init?: RequestInit) => {
    const headers: Record<string, string> = {};
    new Headers(init?.headers).forEach((value, key) => { headers[key] = value; });
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    const req = { url: String(input), method: init?.method ?? "GET", headers, body };
    calls.push(req);
    return respond(req);
  }) as typeof globalThis.fetch;
  return { fetch, calls };
}

const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });

const turn: ChatTurnResult = {
  userMessage: { id: "m1", sessionId: "s1", role: "User", content: "hi", createdAt: "2026-09-03T00:00:00Z" },
  assistantMessage: { id: "m2", sessionId: "s1", role: "Assistant", content: "hello", createdAt: "2026-09-03T00:00:01Z" },
  toolInvocations: [],
  usage: { inputTokens: 10, outputTokens: 5, totalTokens: 15 },
  stopReason: "EndTurn",
};

test("sendMessage posts to the workspace-scoped route and returns the whole turn", async () => {
  const { fetch, calls } = fakeFetch(() => json(turn));
  const client = new FabricateClient({ baseUrl: "https://api.test/", apiKey: "cnc_key", fetch });

  const result = await client.sendMessage("ws1", "s1", "hi");

  assert.equal(calls[0].url, "https://api.test/workspaces/ws1/chat/sessions/s1/messages");
  assert.equal(calls[0].method, "POST");
  assert.equal(calls[0].headers["x-api-key"], "cnc_key");
  assert.deepEqual(calls[0].body, { content: "hi" });
  assert.equal(result.assistantMessage?.content, "hello");
  assert.equal(result.usage.totalTokens, 15);
});

test("createChatSession sends name, mode and optional project", async () => {
  const { fetch, calls } = fakeFetch(() => json({ id: "s1" }));
  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });

  await client.createChatSession("ws1", "Exploration", { mode: "ReviewRequired", projectId: "p1" });

  assert.deepEqual(calls[0].body, { name: "Exploration", projectId: "p1", mode: "ReviewRequired" });
});

test("streamMessage yields SSE events in order and stops at completed", async () => {
  const sse =
    'event: delta\ndata: {"text":"hel"}\n\n' +
    'event: delta\ndata: {"text":"lo"}\n\n' +
    'event: tool_completed\ndata: {"id":"i1","toolName":"discover_schema","status":"Succeeded"}\n\n' +
    `event: completed\ndata: ${JSON.stringify(turn)}\n\n` +
    'event: delta\ndata: {"text":"never seen"}\n\n';
  const { fetch, calls } = fakeFetch(
    () => new Response(sse, { status: 200, headers: { "Content-Type": "text/event-stream" } })
  );
  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });

  const events: ChatStreamEvent[] = [];
  for await (const evt of client.streamMessage("ws1", "s1", "hi")) events.push(evt);

  assert.equal(calls[0].url, "https://api.test/workspaces/ws1/chat/sessions/s1/messages/stream");
  assert.equal(calls[0].headers["accept"], "text/event-stream");
  assert.deepEqual(
    events.map((e) => e.event),
    ["delta", "delta", "tool_completed", "completed"]
  );
  assert.equal(events.filter((e) => e.event === "delta").map((e) => (e.data as { text: string }).text).join(""), "hello");
  assert.equal((events[3].data as ChatTurnResult).assistantMessage?.content, "hello");
});

test("parseSse handles chunk boundaries inside a block and CRLF line endings", async () => {
  const text = 'event: delta\r\ndata: {"text":"a"}\r\n\r\nevent: completed\r\ndata: {"ok":true}\r\n\r\n';
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (let i = 0; i < text.length; i += 7) controller.enqueue(encoder.encode(text.slice(i, i + 7)));
      controller.close();
    },
  });

  const events = [];
  for await (const evt of parseSse(stream)) events.push(evt);

  assert.deepEqual(events, [
    { event: "delta", data: { text: "a" } },
    { event: "completed", data: { ok: true } },
  ]);
});

test("approveToolInvocation returns the invocation and any continuation", async () => {
  const { fetch, calls } = fakeFetch(() =>
    json({ invocation: { id: "i1", status: "Succeeded" }, continuation: turn })
  );
  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });

  const result = await client.approveToolInvocation("ws1", "s1", "i1");

  assert.equal(calls[0].url, "https://api.test/workspaces/ws1/chat/sessions/s1/tool-invocations/i1/approve");
  assert.equal(result.invocation.status, "Succeeded");
  assert.equal(result.continuation?.assistantMessage?.content, "hello");
});

test("LLM credential lifecycle uses the documented routes and verbs", async () => {
  const summary: LlmCredentialSummary = {
    id: "c1", workspaceId: "ws1", projectId: null, name: "team", provider: "Anthropic", kind: "ApiKey",
    fingerprint: "9f3c", lastFour: "a1b2", endpoint: null, model: "claude-opus-5", nonSecretSettings: {},
    isDefault: true, status: "Active", createdAt: "2026-09-03T00:00:00Z", lastValidatedAt: null, lastUsedAt: null, revokedAt: null,
    ownerUserId: null, sessionId: null, isPersonal: false,
  };
  const { fetch, calls } = fakeFetch((req) => {
    if (req.method === "DELETE") return new Response(null, { status: 204 });
    if (req.url.endsWith("/validate")) return json({ credentialId: "c1", isValid: true, message: "ok", checkedAt: "now" });
    if (req.url.endsWith("/policy")) return json({ workspaceId: "ws1", allowPlatformFallback: true, updatedAt: "now", allowedTools: ["discover_schema"] });
    if (req.url.endsWith("/llm-credentials") && req.method === "GET") return json([summary]);
    return json(summary, req.method === "POST" && req.url.endsWith("/llm-credentials") ? 201 : 200);
  });
  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });

  const registered = await client.registerLlmCredential("ws1", { name: "team", provider: "Anthropic", model: "claude-opus-5", secret: "sk-ant-x", isDefault: true });
  const listed = await client.listLlmCredentials("ws1");
  const rotated = await client.rotateLlmCredential("ws1", "c1", "sk-ant-y");
  const validation = await client.validateLlmCredential("ws1", "c1");
  const policy = await client.setWorkspaceLlmPolicy("ws1", { allowPlatformFallback: true, allowedTools: ["discover_schema"] });
  await client.revokeLlmCredential("ws1", "c1");

  assert.equal(registered.lastFour, "a1b2");
  assert.equal(listed.length, 1);
  assert.equal(rotated.id, "c1");
  assert.equal(validation.isValid, true);
  assert.deepEqual(policy.allowedTools, ["discover_schema"]);

  assert.deepEqual(
    calls.map((c) => `${c.method} ${c.url.replace("https://api.test", "")}`),
    [
      "POST /workspaces/ws1/llm-credentials",
      "GET /workspaces/ws1/llm-credentials",
      "POST /workspaces/ws1/llm-credentials/c1/rotate",
      "POST /workspaces/ws1/llm-credentials/c1/validate",
      "PUT /workspaces/ws1/llm-credentials/policy",
      "DELETE /workspaces/ws1/llm-credentials/c1",
    ]
  );
  assert.deepEqual(calls[0].body, { name: "team", provider: "Anthropic", model: "claude-opus-5", secret: "sk-ant-x", isDefault: true });
  assert.deepEqual(calls[2].body, { secret: "sk-ant-y" });
  assert.equal(JSON.stringify(registered).includes("sk-ant"), false, "summaries never echo the secret");
});

test("non-2xx responses become FabricateError with problem details", async () => {
  const { fetch } = fakeFetch(() => json({ title: "Unauthorized", detail: "Only workspace admins can manage LLM credentials." }, 403));
  const client = new FabricateClient({ baseUrl: "https://api.test", apiKey: "k", fetch });

  await assert.rejects(
    () => client.registerLlmCredential("ws1", { name: "x", provider: "Anthropic", model: "m", secret: "s" }),
    (err: unknown) => err instanceof FabricateError && err.status === 403 && err.detail === "Only workspace admins can manage LLM credentials."
  );
});

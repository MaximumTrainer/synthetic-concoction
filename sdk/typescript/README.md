# @fabricate/client

TypeScript SDK for the [Fabricate](https://github.com/MaximumTrainer/synthetic-fabricate) synthetic data API.

## Installation

```bash
npm install @fabricate/client
```

## Quick start

```typescript
import { FabricateClient } from "@fabricate/client";

const client = new FabricateClient({
  baseUrl: "https://your-fabricate-instance.example.com",
  apiKey: "cnc_your_api_key_here",
});

// Create an account and workspace
const account = await client.createAccount("Acme Corp");
const workspace = await client.createWorkspace(account.id, "Data Science");

// Create a project, start a workflow and poll its run
const project = await client.createProject(workspace.id, "Customer Data");
const started = await client.runWorkflow(workspace.id, "wf_xxx");
const run = await client.pollRun(started.id, 2000, 60_000);
console.log("Run status:", run.status);
```

## Agent chat and bring-your-own-key

Chat turns are answered by the LLM the workspace is configured to use. Register your own provider credential once
(the secret is sent once and never returned), then talk to the agent; the response is the whole turn.

```typescript
await client.registerLlmCredential(workspace.id, {
  name: "team-anthropic",
  provider: "Anthropic",
  model: "claude-opus-5",
  secret: process.env.ANTHROPIC_API_KEY!,
  isDefault: true,
});

const session = await client.createChatSession(workspace.id, "Schema exploration");
const turn = await client.sendMessage(workspace.id, session.id, "Discover the schema and summarise it.");
console.log(turn.assistantMessage?.content, turn.usage.totalTokens);

// Or stream it as server-sent events:
for await (const evt of client.streamMessage(workspace.id, session.id, "Generate 50 rows per table.")) {
  if (evt.event === "delta") process.stdout.write(evt.data.text);
}
```

`ReviewRequired` sessions park tool calls until `approveToolInvocation` runs them; once every parked call is decided
the model resumes and the resulting turn comes back as `continuation`.

## Error handling

```typescript
import { FabricateClient, FabricateError } from "@fabricate/client";

try {
  await client.getAccount("unknown-id");
} catch (err) {
  if (err instanceof FabricateError) {
    console.error(`API error ${err.status}: ${err.detail}`);
  }
}
```

## Authentication

All requests are authenticated via the `X-Api-Key` header. Create an API key through the dashboard or the `createApiKey` method. The plaintext secret is returned only once at creation time.

## `pollRun` helper

`pollRun` polls a dataset run at a configurable interval until it reaches a terminal state:

```typescript
const completedRun = await client.pollRun(runId, {
  intervalMs: 3000,   // poll every 3 seconds (default: 2000)
  timeoutMs: 300_000, // give up after 5 minutes (default: 120_000)
});
```

## Building from source

```bash
npm install
npm run build
```

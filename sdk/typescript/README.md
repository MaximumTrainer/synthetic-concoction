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

// Create a project and poll a run
const project = await client.createProject(workspace.id, "Customer Data");
const { runId } = await client.runWorkflow("wf_xxx");
const run = await client.pollRun(runId, { timeoutMs: 60_000 });
console.log("Run status:", run.status);
```

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

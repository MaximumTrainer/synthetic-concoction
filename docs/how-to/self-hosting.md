# Self-hosting Fabricate

Fabricate runs entirely on infrastructure you control. It ships with **no embedded credentials**: you bring your own
LLM key (or cloud identity), your own PostgreSQL, and your own API key for the first login. This guide covers the
configuration contract, the one-command local setup, the Fly.io reference deployment, the documented alternatives, and
what leaves the instance over the network.

## Configuration contract

Everything is configured by environment variables, so the same image runs unchanged on any host.

### Core

| Variable | Required | Purpose |
| --- | --- | --- |
| `FABRICATE__BootstrapApiKey` | yes, first run | Seeds a bootstrap account and API key so you can authenticate. Generate with `openssl rand -base64 32`. |
| `FABRICATE_DB_PROVIDER` | yes | `postgres` for any hosted deployment; `sqlite` for local experiments; unset = in-memory (state lost on restart). |
| `FABRICATE_CONNECTION_STRING` | with `postgres` | PostgreSQL connection string. Carries credentials — set it as a **secret**. |
| `FABRICATE_DATA_PROTECTION_KEYS_PATH` | recommended | Directory for the Data Protection key ring that encrypts tenant LLM credentials. Must persist across restarts and be shared across instances; see [Key ring](#key-ring). |
| `ASPNETCORE_URLS` | no | Defaults to `http://+:8080` in the image. TLS is terminated by your platform. |

Pending EF Core migrations are applied automatically at startup, so a fresh database needs no manual step. Several
instances starting at once apply the schema exactly once (EF Core's migration lock).

### Operator (platform) LLM credential — `FABRICATE_LLM_*`

Optional. Leave `FABRICATE_LLM_PROVIDER` unset and the platform credential is disabled; workspaces can still
[bring their own keys](byok-llm-credentials.md). If it **is** set and invalid, the API refuses to start and names the
offending variable.

| Variable | Required | Purpose |
| --- | --- | --- |
| `FABRICATE_LLM_PROVIDER` | to enable | `anthropic` \| `openai-compatible` \| `bedrock` \| `vertex` \| `foundry` |
| `FABRICATE_LLM_MODEL` | yes | Default model id, e.g. `claude-opus-5` |
| `FABRICATE_LLM_ALLOWED_MODELS` | yes | Comma-separated allowlist; `FABRICATE_LLM_MODEL` must be a member. Also constrains models that workspaces may register. |
| `FABRICATE_LLM_API_KEY_SECRET` | `anthropic`, `foundry` | The **name** of the variable holding the key (e.g. `ANTHROPIC_API_KEY`) — never the key itself. |
| `FABRICATE_LLM_BASE_URL` | `openai-compatible`, `foundry` | Endpoint base URL. |
| `FABRICATE_LLM_REGION` | `bedrock` | AWS region; credentials come from the ambient IAM identity. |
| `FABRICATE_LLM_PROJECT_ID`, `FABRICATE_LLM_LOCATION` | `vertex` | GCP project and location; credentials come from Application Default Credentials. |
| `FABRICATE_LLM_PLATFORM_FALLBACK` | no | `always` (single-operator self-hosting), `workspace-opt-in` (default; multi-tenant), `never`. |
| `FABRICATE_LLM_EFFORT` | no | `low` \| `medium` \| `high` \| `max` where the provider supports it. |
| `FABRICATE_LLM_MAX_OUTPUT_TOKENS` | no | Default 16000. |
| `FABRICATE_LLM_TIMEOUT_SECONDS` | no | Default 120. Set your platform's proxy timeout above this. |
| `FABRICATE_LLM_MAX_TOOL_ITERATIONS` | no | Default 8. Caps the model/tool loop per turn. |
| `FABRICATE_LLM_HISTORY_WINDOW` | no | Default 40 messages sent as context. |
| `FABRICATE_LLM_ALLOWED_ENDPOINT_HOSTS` | no | Hosts that workspace-supplied endpoints may target (suffix match). Empty = any public HTTPS host. |
| `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS` | no | `true` permits `http://` and private/loopback endpoints — for air-gapped local runtimes only. |

Provider notes:

- **anthropic** — the official Anthropic API. The adapter sends adaptive thinking and effort and never sends sampling
  parameters or `budget_tokens`, which current Claude models reject.
- **openai-compatible** — one adapter for OpenAI, Azure OpenAI, vLLM, Ollama and gateways such as OpenRouter. A
  keyless local runtime works with `FABRICATE_LLM_API_KEY_SECRET` unset.
- **bedrock / vertex / foundry** — Claude through your cloud account, authenticated by IAM / ADC / a Foundry key.
  Bedrock model ids take the `anthropic.` prefix (e.g. `anthropic.claude-opus-5`); Vertex uses the bare id.

### Which credential a chat turn uses

Project-bound credential → workspace default for the provider → the workspace's single active credential → the
platform credential (only where `FABRICATE_LLM_PLATFORM_FALLBACK` allows) → none, in which case the chat returns a
clear notice and the direct `/tool` commands still work.

## One command locally

```bash
cp .env.example .env        # fill in FABRICATE__BootstrapApiKey and your LLM key
docker compose up --build
curl -s http://localhost:8080/healthz
```

Compose runs the API against PostgreSQL with a persisted volume for the Data Protection key ring, so the local shape is
the hosted shape. Swagger UI is at `http://localhost:8080/swagger`.

## Fly.io (reference deployment)

Why Fly: it runs the unchanged image, offers PostgreSQL on a private network next to the API, injects secrets as
environment variables, health-checks `/healthz`, and scales to zero.

1. Fork the repository and install `flyctl`.
2. `fly launch --no-deploy` (accepts the checked-in `fly.toml`; choose your app name and region).
3. Create and attach PostgreSQL — this sets `FABRICATE_CONNECTION_STRING` as a secret for you:
   ```bash
   fly postgres create --name fabricate-db --region lhr
   fly postgres attach fabricate-db --app fabricate --variable-name FABRICATE_CONNECTION_STRING
   ```
   (Or use Fly Managed Postgres, or set the secret by hand for Neon/Supabase — with `SSL Mode=Require`.)
4. Set the remaining secrets — these never enter GitHub:
   ```bash
   fly secrets set FABRICATE__BootstrapApiKey="$(openssl rand -base64 32)"
   fly secrets set ANTHROPIC_API_KEY="sk-ant-…"
   ```
5. In GitHub: add `FLY_API_TOKEN` (`fly tokens create deploy -x 999999h`) as a secret, `SMOKE_API_KEY` (the bootstrap
   key) as a secret in an environment named `fly`, and optionally `FLY_APP` as a variable.
6. Push to `main`. The [deploy workflow](../../.github/workflows/deploy-fly.yml) builds remotely, deploys, waits for
   `/healthz` through the cold start, and runs the smoke tests — and fails if they were skipped rather than executed.

Rotating any secret is `fly secrets set …`; it restarts the machine and needs no redeploy. Scaling out is
`fly scale count 2` — the API is stateless.

### Cold start

With `min_machines_running = 0` the first request after idle starts a machine. Measure it after your first deploy:

```bash
fly machine stop; time curl -s -o /dev/null https://<app>.fly.dev/healthz
```

Record the number in your runbook and set `grace_period` in `fly.toml` a little above it. Expect single-digit seconds
for the default image; if it is materially longer, trim the image (ReadyToRun/trimmed publish) rather than raising
`min_machines_running`.

### Key ring

Per-workspace LLM credentials are encrypted with ASP.NET Core Data Protection. The key ring at
`FABRICATE_DATA_PROTECTION_KEYS_PATH` is what decrypts them: if it is lost, stored credentials are unrecoverable (users
re-register them). On Fly's ephemeral disk the checked-in path is fine for a **single-operator instance that uses the
platform credential**; before relying on per-workspace credentials in production, move the key ring to shared,
persistent storage (a Fly volume on a single machine, or a blob store with an external KMS-wrapped key) — Data
Protection supports both.

## Alternatives

- **Render** — `render.yaml` at the repository root is a Blueprint: a Docker web service plus a managed PostgreSQL,
  with secrets prompted in the dashboard.
- **Railway** — create a service from the repository (Dockerfile auto-detected) and add the PostgreSQL plugin; set the
  same variables as `.env.example`, with `FABRICATE_CONNECTION_STRING` from the plugin's connection URL.
- **Cloudflare Containers + Neon** — viable now that the API is stateless; documented only, no maintained pipeline.
  Cloudflare **Workers** cannot run .NET; Cloudflare **Pages** is a good host for this `docs/` site.
- **AWS / Azure / GCP with Terraform** — the production-grade path under `infra/`. Set `FABRICATE_DB_PROVIDER=postgres`
  and the connection string secret so the provisioned database is actually used.

## Egress profile

A running instance makes outbound connections **only** to:

| Destination | When |
| --- | --- |
| Your PostgreSQL | always |
| The configured LLM endpoint | during chat turns, credential validation, and nothing else |
| Databases you point schema discovery at | when you run discovery |

Selecting `openai-compatible` with a private base URL and `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS=true` yields an
instance that makes no calls outside your network. Schema metadata (table, column, type and relationship names) is
sent to the model as tool output; row values are not.

## Cost (Fly reference configuration)

Assumptions: one `shared-cpu-1x` 512 MB machine that stops when idle, the smallest PostgreSQL plan, light usage. The
database is the floor of the bill because it does not scale to zero; an external free-tier PostgreSQL (e.g. Neon) is
the lowest-cost option. Check Fly's current pricing page for figures — they change more often than this document.

## Health and readiness

`GET /healthz` (unauthenticated) reports `status` and an `llm` block — whether the platform credential is configured,
its provider and model, and the fallback mode. It never includes a secret or the resolved connection string. A missing
LLM credential does not fail health: the rest of the API does not depend on a model.

## Backups

Use your PostgreSQL provider's automated backups and record the retention window. Rehearse a restore into a fresh
database followed by an API boot against it; the migrator will find nothing pending and the bootstrap key will still
authenticate.

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
| `FABRICATE_API_RATE_LIMIT_PER_MINUTE` | no | Requests per minute per API key across every authenticated route (default 100). `/healthz` and Swagger are exempt. Exceeding it returns `429` with `Retry-After`. |
| `FABRICATE_ARTIFACTS_PATH` | no | Directory for generated artifacts (CSV/JSON/SQL/Parquet). Defaults to the OS temp directory, which is ephemeral on every hosted platform; mount a volume here if artifacts must survive a restart, or use object storage (below). |
| `FABRICATE_ARTIFACT_STORE` | no | `filesystem` (default) or `s3`. See [Artifact storage](#artifact-storage). |
| `FABRICATE_ARTIFACT_BUCKET` | with `s3` | Bucket generated artifacts are written to. |
| `FABRICATE_ARTIFACT_S3_ENDPOINT` | for non-AWS | Endpoint URL for MinIO, Cloudflare R2 or Backblaze B2. Unset means AWS. |
| `FABRICATE_ARTIFACT_S3_REGION` | for AWS | Region. Still required by request signing even where the store ignores it. |
| `FABRICATE_ARTIFACT_S3_FORCE_PATH_STYLE` | for non-AWS | `true` for MinIO and most S3-compatible stores, which do not support virtual-host addressing. |
| `FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET` | no | Secret **name** holding the access key. Omit both key variables to use ambient cloud identity. |
| `FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET` | no | Secret **name** holding the secret key. |
| `FABRICATE_ARTIFACT_RETENTION_DAYS` | no | Days to keep generated artifacts. Default `0` keeps them. |

| `FABRICATE_AUDIT_RETENTION_DAYS` | no | Days of audit history to keep. **Default `0` keeps everything**, so an existing deployment never starts deleting on upgrade. Set it and a background sweep purges anything older; see [Audit retention](#audit-retention). |
| `FABRICATE_AUDIT_SWEEP_MINUTES` | no | How often the retention sweep runs (default 360, six hours). Only read when retention is enabled. |
| `FABRICATE_AUDIT_PURGE_BATCH_SIZE` | no | Rows deleted per statement (default 1000), so clearing a long backlog does not hold one long write lock. |
| `FABRICATE_API_USAGE_SAMPLING` | no | Fraction of authenticated requests recorded as `api.request` audit events, 0.0–1.0. Default `1.0` records every request; `0` switches per-request usage auditing off. A busy deployment that wants the signal without the volume can sample — but note a sampled log cannot answer "did this key call that endpoint", only "how often, roughly". |

### Audit retention

Audit events are insert-only and otherwise grow forever, which on a hosted deployment eventually costs more than
the log is worth. Retention is **off by default**: `FABRICATE_AUDIT_RETENTION_DAYS=0` keeps every event.

Setting it to a positive number starts a background sweep that deletes events strictly older than the window,
across every account, in batches. An event exactly on the boundary is kept. The sweep audits itself as
`audit.retention_applied`, recording the window, the cutoff and the number removed — and records nothing when it
deleted nothing, so an idle sweep does not become its own source of growth.

```bash
FABRICATE_AUDIT_RETENTION_DAYS=90    # keep a quarter of history
FABRICATE_AUDIT_SWEEP_MINUTES=360    # sweep every six hours (the default)
```

Deleting audit history may itself be regulated in your jurisdiction. Export before you shorten a window — see
[Exporting the audit log](../user-guide.md#exporting-the-audit-log) — and keep the export somewhere the retention
window does not reach.

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
| `FABRICATE_LLM_MAX_INPUT_TOKENS` | no | Default 120000. Estimated input budget per request; oldest history is dropped to fit. `0` disables trimming. |
| `FABRICATE_LLM_MAX_RETRIES` | no | Default 2. Retries of retryable provider failures (rate limit, transport, timeout, 5xx) with exponential backoff from 500 ms. Authentication and invalid-request failures are never retried. |
| `FABRICATE_LLM_ALLOWED_ENDPOINT_HOSTS` | no | Hosts that workspace-supplied endpoints may target (suffix match). Empty = any public HTTPS host. |
| `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS` | no | `true` permits `http://` and private/loopback endpoints — for air-gapped local runtimes only. |

Provider notes:

- **anthropic** — the official Anthropic API. The adapter sends adaptive thinking and effort and never sends sampling
  parameters or `budget_tokens`, which current Claude models reject.
- **openai-compatible** — one adapter for OpenAI, Azure OpenAI, Gemini's OpenAI-compatible endpoint, vLLM, Ollama
  and gateways such as OpenRouter. A keyless local runtime works with `FABRICATE_LLM_API_KEY_SECRET` unset. Azure
  OpenAI is recognised by its host: the key is sent as the `api-key` header Azure requires, and a bare resource URL
  is completed with `/openai/deployments/<model>/chat/completions?api-version=…` (the model id is the deployment name).
- **bedrock / vertex / foundry** — Claude through your cloud account, authenticated by IAM / ADC / a Foundry key.
  Bedrock model ids take the `anthropic.` prefix (e.g. `anthropic.claude-opus-5`); Vertex uses the bare id.

### Which credential a chat turn uses

Project-bound credential → workspace default for the provider → the workspace's single active credential → the
platform credential (only where `FABRICATE_LLM_PLATFORM_FALLBACK` allows) → none, in which case the chat returns a
clear notice and the direct `/tool` commands still work.


### Artifact storage

Generated artifacts default to the local file system (`FABRICATE_ARTIFACTS_PATH`, else the OS temp directory).
That is right for local use and **wrong on every hosted target**: Fly machines are replaced, Cloud Run and
Container Apps revisions are immutable, ECS tasks restart. The files disappear and a completed run is left
pointing at artifacts that no longer exist.

Set `FABRICATE_ARTIFACT_STORE=s3` to use object storage instead. One adapter covers **AWS S3, MinIO, Cloudflare
R2 and Backblaze B2**, because they all speak the same API:

```bash
# AWS S3, using the task or instance role — no keys stored anywhere
FABRICATE_ARTIFACT_STORE=s3
FABRICATE_ARTIFACT_BUCKET=acme-fabricate-artifacts
FABRICATE_ARTIFACT_S3_REGION=eu-west-1

# MinIO, Cloudflare R2 or Backblaze B2
FABRICATE_ARTIFACT_STORE=s3
FABRICATE_ARTIFACT_BUCKET=fabricate-artifacts
FABRICATE_ARTIFACT_S3_ENDPOINT=https://s3.example.internal
FABRICATE_ARTIFACT_S3_FORCE_PATH_STYLE=true
FABRICATE_ARTIFACT_S3_ACCESS_KEY_SECRET=ARTIFACT_ACCESS_KEY
FABRICATE_ARTIFACT_S3_SECRET_KEY_SECRET=ARTIFACT_SECRET_KEY
```

**Credentials.** Ambient cloud identity first — an IAM role on ECS or EKS, or the instance profile — because that
means no key is stored anywhere at all. The two `*_SECRET` variables are the fallback for stores that have no
ambient identity, and they hold the *name* of a secret, not its value, so artifact keys follow the same path as
every other secret. Setting one without the other is refused at startup rather than falling through to ambient
credentials and failing later with an unrelated permissions error.

The configuration is validated when the container is built, so a mistake stops the instance starting rather than
being discovered by the first person to generate data.

**Uploads and downloads stream.** Size and SHA-256 are computed as the bytes pass through and stored as object
metadata, which is what lets the run manifest be served without reading the blobs.

**Retention.** `FABRICATE_ARTIFACT_RETENTION_DAYS` defaults to `0`, keeping everything. A positive value starts a
sweep that deletes the artifacts of runs older than the window; the run record keeps its checksums — still the
record of what was produced — but reports an empty artifact manifest rather than paths that no longer resolve.
Where your object store offers a lifecycle policy, configuring one on the bucket is cheaper than this sweep and
does the same job; the sweep exists for stores that do not, and for operators who prefer the rule in one place.

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
- **AWS / Azure / GCP with Terraform** — the production-grade path under `infra/`. The stacks set
  `FABRICATE_DB_PROVIDER=postgres` and `FABRICATE_CONNECTION_STRING` to the database they provision, so state is
  durable there too; add the `FABRICATE_LLM_*` variables and your key secret to enable chat.

## Egress profile

A running instance makes outbound connections **only** to:

| Destination | When |
| --- | --- |
| Your PostgreSQL | always |
| The configured LLM endpoint | during chat turns, credential validation, and nothing else |
| Databases you point schema discovery at | when you run discovery |

Selecting `openai-compatible` with a private base URL and `FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS=true` yields an
instance that makes no calls outside your network.

### The prompt data boundary

What may reach a model provider is enforced in code, not left to convention. Every tool declares the most
sensitive class of content its result can carry, and a tool whose class the boundary forbids is **never offered to
the model** — so the model cannot ask for it and be refused halfway through a turn, which would both disclose that
the data exists and leave the user with a broken conversation.

| Content class | What it is | When it may be sent |
| --- | --- | --- |
| `Metadata` | Table, column, type and relationship names; run summaries. Describes the data rather than containing it. | Always. Without it the agent is useless. |
| `AggregateStatistics` | Histograms, distinct counts, min/max over real rows. No single row is disclosed, but a min/max is a real value and a histogram over a small table can identify individuals. | Only with the workspace opt-in. |
| `SampledValues` | Values copied from real rows — samples, examples, few-shot rows. | Only with the workspace opt-in. |

The opt-in is `allowSampledDataInPrompts` on the workspace LLM policy
(`PUT /workspaces/{id}/llm-credentials/policy`), and it defaults to **false**.

**It cannot be enabled at all on a `Healthcare` or `Finance` workspace.** The request is refused with `409` and an
explanation, and the policy is left exactly as it was — refused rather than silently ignored, because an
administrator told "saved" while the setting did not take is worse off than one told why it cannot be. A
workspace's compliance profile is fixed when it is created. The profile is also re-checked at every decision, so
an opt-in written before a profile changed does not survive the change.

Every refusal is audited as `llm.boundary_blocked` with the tool name and content class, and never the payload.

Today's tools all return `Metadata`, so the boundary changes nothing for them. It exists ahead of the tools that
will need it — NoSQL discovery samples documents to infer field types, data profilers compute per-column
statistics, and any future "explain this data" tool sends values by construction.

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

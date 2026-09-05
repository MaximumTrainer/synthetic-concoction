# Bring your own LLM key (BYOK)

A workspace can supply its own LLM provider credentials so the Data Agent chat runs against **that** provider account —
your billing, your quota, your data-residency terms — without the operator holding a key on your behalf. Credentials
are encrypted at rest, write-only through the API, and never appear in logs, responses or audit records.

## Concepts

- **Credential** — one provider + model + secret (or cloud identity), scoped to a workspace, optionally bound to a
  project, optionally marked the workspace *default* for its provider.
- **Summary** — what every read returns: fingerprint, last four characters, provider, model, status. Never the secret.
- **Policy** — a per-workspace switch (`allowPlatformFallback`) that lets the operator's platform credential be used
  when the workspace has none. Off by default; the operator can also force it on or off instance-wide with
  `FABRICATE_LLM_PLATFORM_FALLBACK`.

## Which credential a turn uses

1. A **personal** credential the requesting member bound to this session.
2. A **personal** credential the requesting member registered for this workspace.
3. A credential bound to the session's project.
4. The workspace default for the provider.
5. The workspace's single active credential, if there is exactly one.
6. The platform credential, if the workspace policy (or the instance setting) allows it.
7. None — the chat returns a notice explaining how to register one; `/tool …` commands still work.

Revoked credentials are never resolved, and revocation takes effect on the next turn.

## Personal credentials

By default every member of a workspace shares one key, one bill and one quota. A member who holds their own
provider account — or a contractor who should not spend the workspace's quota — can register a **personal**
credential instead:

```http
POST /workspaces/{workspaceId}/llm-credentials
{
  "name": "my-anthropic",
  "provider": "Anthropic",
  "model": "claude-opus-5",
  "secret": "sk-ant-…",
  "isPersonal": true,
  "sessionId": null
}
```

| | Shared credential | Personal credential |
|---|---|---|
| Who can register | Workspace **Admin** | Any workspace member, for themselves |
| Who can read, rotate or validate | Workspace Admin | **Only the owner** |
| Who can revoke | Workspace Admin | The owner, or a workspace Admin |
| Who sees it in a list | Every member | The owner, plus Admins as a redacted summary |
| Whose sessions use it | Everyone's, by precedence | Only the owner's |

Setting `sessionId` binds the credential to one chat session; it is used for that session and no other.

Names are unique **within their scope**, so two members may each have one called `default`.

An Admin can see that a member holds a personal credential — owner, provider, fingerprint, last four, status — for
governance, and can revoke it so that offboarding does not need the member's cooperation. An Admin cannot read,
rotate, validate or use it: validation makes a real provider call, so it counts as using it.

A personal credential stops being usable in three ways, none of which needs a cleanup job:

- Its owner **loses access to the workspace**. Access is re-checked on every resolve, because it can also be lost
  by a group membership changing, and a cleanup that can be missed is not a control.
- A workspace Admin **turns personal credentials off** on the policy.
- It is revoked.

A personal credential is never picked up by the workspace rungs, so one member's key can never become everyone's
fallback.

## Endpoints

All under `/workspaces/{workspaceId}/llm-credentials` and authenticated with an API key. Register, rotate, revoke and
policy changes require the workspace **Admin** role, except for personal credentials, which their owner manages
themselves (see [Personal credentials](#personal-credentials)); list needs any workspace role. A credential id
belonging to another workspace returns `404`, never `403`, so there is no existence oracle.

### Register

```http
POST /workspaces/{workspaceId}/llm-credentials
{
  "name": "team-anthropic",
  "provider": "Anthropic",
  "model": "claude-opus-5",
  "secret": "sk-ant-…",
  "isDefault": true
}
```

Provider-specific shapes:

| Provider | `kind` | `secret` | `endpoint` | `nonSecretSettings` |
| --- | --- | --- | --- | --- |
| `Anthropic` | `ApiKey` | API key | optional base URL | — |
| `OpenAiCompatible` | `ApiKey` | API key (may be empty for a keyless local runtime) | **required**, e.g. `https://api.openai.com/v1` | — |
| `OpenAiCompatible` — **Azure OpenAI** | `ApiKey` | Azure OpenAI key (sent as `api-key`, which Azure requires) | `https://<resource>.openai.azure.com` (the `model` is used as the deployment name) or the full deployment URL with `?api-version=` | — |
| `OpenAiCompatible` — **Gemini** | `ApiKey` | Google AI API key | `https://generativelanguage.googleapis.com/v1beta/openai` | — |
| `AwsBedrock` | `CloudIdentity` | empty | — | `region` |
| `GcpVertexAi` | `CloudIdentity` | empty | — | `projectId`, `location` |
| `AzureFoundry` | `ApiKey` | Foundry key | resource endpoint | `resourceName` (optional; derived from the endpoint) |

The response is a summary:

```json
{ "id": "…", "name": "team-anthropic", "provider": "Anthropic", "model": "claude-opus-5",
  "fingerprint": "9f3c…", "lastFour": "a1b2", "isDefault": true, "status": "Active", … }
```

Rules enforced on registration: the name is unique among live credentials in the workspace (a revoked name can be
reused); the model must be in the instance allowlist (`FABRICATE_LLM_ALLOWED_MODELS`); API-key credentials need a
non-empty secret; `OpenAiCompatible` and `AzureFoundry` need an endpoint.

### Endpoint safety

A workspace-supplied endpoint is an egress target for the workspace's own secret, so it is validated as a public HTTPS
host: `http://`, loopback, private (RFC 1918), link-local (including cloud metadata addresses) and single-label hosts
are rejected, and if the operator set `FABRICATE_LLM_ALLOWED_ENDPOINT_HOSTS`, the host must match one of those
suffixes. Air-gapped deployments can opt in to private endpoints instance-wide with
`FABRICATE_LLM_ALLOW_PRIVATE_ENDPOINTS=true`.

### List

```http
GET /workspaces/{workspaceId}/llm-credentials
```

### Validate

```http
POST /workspaces/{workspaceId}/llm-credentials/{id}/validate
```

Makes the smallest possible call to the provider (a 16-token completion) under a 20-second timeout and returns
`{ "isValid": true|false, "message": "…" }`. Updates the credential's `status` and `lastValidatedAt`. Rate-limited to
10 calls per minute per client so the endpoint cannot be used to test stolen keys.

### Rotate

```http
POST /workspaces/{workspaceId}/llm-credentials/{id}/rotate
{ "secret": "sk-ant-new…" }
```

Replaces the ciphertext, fingerprint and last-four in place; the id is stable, so project bindings survive.

### Revoke

```http
DELETE /workspaces/{workspaceId}/llm-credentials/{id}
```

Soft: the row is kept with `status: Revoked` and `revokedAt` for the audit trail, and is excluded from resolution
immediately.

### Policy

```http
GET  /workspaces/{workspaceId}/llm-credentials/policy
PUT  /workspaces/{workspaceId}/llm-credentials/policy
{
  "allowPlatformFallback": true,
  "allowPersonalCredentials": false,
  "dailyTokenBudget": 200000,
  "monthlyTokenBudget": 4000000
}
```

Omit a field to leave it unchanged. `allowPersonalCredentials` defaults to `true`; setting it to `false` both
blocks new personal credentials and makes existing ones unresolvable immediately — a workspace that must run
everything through one shared, audited key gets that with a single switch, and it takes effect on the next turn
rather than after a cleanup.

Budgets are in [the user guide](../user-guide.md#llm-usage-and-token-budgets).

## Storage and encryption

Secrets are encrypted with ASP.NET Core Data Protection under a versioned purpose (`KeyVersion` is stored with each
row so the cipher can change later without rewriting rows). The key ring lives at
`FABRICATE_DATA_PROTECTION_KEYS_PATH`, outside the database, so a database dump alone cannot decrypt credentials. The
decrypted secret exists only for the duration of a request, inside a carrier whose `ToString()` omits it.

## Audit

`llm_credential.registered`, `.rotated`, `.revoked`, `.validated` / `.validation_failed` and `llm_policy.updated`
are written to the account audit log with actor, workspace, credential id, provider, model and fingerprint — never the
secret.

## Self-hosted and CLI use

A single-operator instance does not need per-workspace credentials at all: set the `FABRICATE_LLM_*` variables and
`FABRICATE_LLM_PLATFORM_FALLBACK=always` as described in [Self-hosting](self-hosting.md), and every workspace uses the
platform credential. Per-workspace credentials still work on top and take precedence.

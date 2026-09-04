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

1. A credential bound to the session's project.
2. The workspace default for the provider.
3. The workspace's single active credential, if there is exactly one.
4. The platform credential, if the workspace policy (or the instance setting) allows it.
5. None — the chat returns a notice explaining how to register one; `/tool …` commands still work.

Revoked credentials are never resolved, and revocation takes effect on the next turn.

## Endpoints

All under `/workspaces/{workspaceId}/llm-credentials` and authenticated with an API key. Register, rotate, revoke and
policy changes require the workspace **Admin** role; list and validate need any workspace role. A credential id
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
{ "allowPlatformFallback": true }
```

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

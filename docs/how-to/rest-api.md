# REST API Reference

The Fabricate REST API is an ASP.NET Core Minimal API. It listens on port 5000 by default and exposes Swagger UI at `/swagger`.

## Authentication

All endpoints require the `X-Api-Key` header with a valid API key:

```http
X-Api-Key: cnc_yoursecrethere
```

Keys are created via `POST /accounts/{accountId}/api-keys`. The plaintext secret is shown only once at creation time; the platform stores only the SHA-256 hash.

Missing or invalid keys return `401 Unauthorized`.

## Rate Limiting

Fixed window: **100 requests per minute**. Exceeding the limit returns `429 Too Many Requests`.

## Base URL

```
http://localhost:5000
```

Replace with your deployed URL in production.

## Swagger UI

```
http://localhost:5000/swagger
```

All routes are described with OpenAPI annotations.

---

## Accounts

### POST /accounts

Create a new account.

**Request:**

```http
POST /accounts
X-Api-Key: cnc_...
Content-Type: application/json

{ "name": "Acme Corp" }
```

**Response 200:**

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Acme Corp",
  "createdAt": "2024-06-01T10:00:00Z"
}
```

---

### GET /accounts/{id}

Get account details.

**Response 200:**

```json
{
  "id": "3fa85f64-...",
  "name": "Acme Corp",
  "createdAt": "2024-06-01T10:00:00Z"
}
```

**Response 404:** Account not found.

---

### GET /accounts/{id}/members

List members of an account.

**Response 200:**

```json
[
  {
    "accountId": "3fa85f64-...",
    "userId": "usr_alice",
    "role": "Owner",
    "joinedAt": "2024-06-01T10:00:00Z"
  }
]
```

---

### POST /accounts/{id}/invitations

Invite a user to an account.

**Request:**

```json
{
  "inviteeEmail": "bob@example.com",
  "expiry": "48:00:00"
}
```

**Response 200:** Returns the created invitation object with a time-limited token.

---

### POST /accounts/invitations/accept

Accept an invitation using the token from the invitation email.

**Request:**

```json
{ "token": "invite_abc123..." }
```

**Response 200:** Returns the new `AccountMembership`.

---

## Workspaces

### POST /workspaces

Create a workspace under an account.

**Request:**

```json
{
  "accountId": "3fa85f64-...",
  "name": "Production Clone"
}
```

**Response 200:**

```json
{
  "id": "9a3b2c1d-...",
  "accountId": "3fa85f64-...",
  "name": "Production Clone",
  "createdAt": "2024-06-01T10:05:00Z"
}
```

---

### GET /workspaces/{id}

Get workspace details.

---

### POST /workspaces/{id}/members

Add a member to a workspace with a role.

**Request:**

```json
{
  "userId": "usr_bob",
  "role": "Editor"
}
```

Roles: `Viewer`, `Editor`, `Admin`.

---

### DELETE /workspaces/{id}/members/{userId}

Remove a member from a workspace.

**Response 204:** No content.

---

## Projects

### POST /projects

Create a project under a workspace.

**Request:**

```json
{
  "workspaceId": "9a3b2c1d-...",
  "name": "Orders Dataset"
}
```

**Response 200:**

```json
{
  "id": "proj-001",
  "workspaceId": "9a3b2c1d-...",
  "name": "Orders Dataset",
  "isArchived": false,
  "createdAt": "2024-06-01T10:10:00Z"
}
```

---

### GET /projects/{id}

Get project details.

**Response 404:** Project not found or archived.

---

### DELETE /projects/{id}

Soft-delete (archive) a project.

**Response 200:** Returns the archived project with `isArchived: true`.

---

## Runs

### GET /runs

List dataset runs with pagination.

**Query Parameters:**

| Parameter | Default | Description |
|---|---|---|
| `page` | 1 | Page number (1-based) |
| `pageSize` | 20 | Results per page |

**Response 200:**

```json
{
  "items": [
    {
      "id": "run-abc123",
      "status": "Completed",
      "createdAt": "2024-06-01T10:00:00Z",
      "startedAt": "2024-06-01T10:00:01Z",
      "completedAt": "2024-06-01T10:00:05Z",
      "seed": 42,
      "requestedRowCounts": { "public.users": 100 }
    }
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 20
}
```

---

### GET /runs/{id}

Get a specific run.

**Run Status Values:**

| Status | Description |
|---|---|
| `Queued` | Awaiting execution |
| `Running` | In progress |
| `Completed` | Finished successfully |
| `Failed` | Terminated with error |
| `Cancelled` | Cancelled before completion |

---

### POST /runs/{id}/cancel

Cancel a queued or running run.

**Response 200:** Returns the updated run.
**Response 409:** Run is not in a cancellable state.

---

## Workflows

### POST /workflows

Create a workflow.

**Request:**

```json
{
  "workspaceId": "9a3b2c1d-...",
  "name": "Nightly Seed Refresh",
  "steps": [
    { "type": "generate", "rows": 500 },
    { "type": "export", "format": "sql" }
  ]
}
```

**Response 200:**

```json
{
  "id": "wf-001",
  "workspaceId": "9a3b2c1d-...",
  "name": "Nightly Seed Refresh",
  "isDisabled": false,
  "createdAt": "2024-06-01T10:00:00Z"
}
```

---

### GET /workflows/{id}

Get workflow details.

---

### POST /workflows/{id}/run

Trigger a workflow execution.

**Response 200:**

```json
{ "runId": "run-xyz789" }
```

Use `GET /runs/{runId}` to track progress.

---

### GET /workflows/{id}/runs

List all runs for a workflow.

---

## Chat

### GET /chat/sessions

List chat sessions for the authenticated user.

---

### POST /chat/sessions

Create a new chat session.

**Request:**

```json
{
  "workspaceId": "9a3b2c1d-...",
  "title": "Schema exploration"
}
```

**Response 200:**

```json
{
  "id": "sess-001",
  "workspaceId": "9a3b2c1d-...",
  "title": "Schema exploration",
  "mode": "standard",
  "archivedAt": null,
  "createdAt": "2024-06-01T10:00:00Z"
}
```

---

### POST /chat/sessions/{id}/messages

Send a message to a chat session.

**Request:**

```json
{ "content": "Discover the schema for this database." }
```

**Response 200:**

```json
{
  "id": "msg-001",
  "sessionId": "sess-001",
  "role": "assistant",
  "content": "I found 3 tables: users, orders, order_items. Here are the details...",
  "createdAt": "2024-06-01T10:00:01Z"
}
```

---

## API Keys

### POST /accounts/{accountId}/api-keys

Create an API key.

**Request:**

```json
{
  "name": "ci-pipeline",
  "scopes": ["workspace:read", "workspace:write"],
  "expiry": "90.00:00:00"
}
```

**Response 200:**

```json
{
  "id": "3fa85f64-...",
  "name": "ci-pipeline",
  "plaintextSecret": "cnc_abc123...",
  "scopes": ["workspace:read", "workspace:write"],
  "expiresAt": "2024-09-01T00:00:00Z"
}
```

The `plaintextSecret` is shown **only once**. Store it securely.

---

### GET /accounts/{accountId}/api-keys

List API keys for an account (metadata only, no secrets).

**Response 200:**

```json
[
  {
    "id": "3fa85f64-...",
    "name": "ci-pipeline",
    "scopes": ["workspace:read"],
    "expiresAt": "2024-09-01T00:00:00Z",
    "isRevoked": false,
    "createdAt": "2024-06-01T10:00:00Z"
  }
]
```

---

### DELETE /accounts/{accountId}/api-keys/{keyId}

Revoke an API key. The key is immediately invalid.

**Response 200:** Returns the revoked key with `isRevoked: true`.

---

## Error Responses

All error responses follow the RFC 7807 Problem Details format:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.4",
  "title": "Not Found",
  "status": 404,
  "detail": "Account 3fa85f64-... was not found."
}
```

| Status | Meaning |
|---|---|
| 400 | Bad request (validation failed) |
| 401 | Missing or invalid API key |
| 403 | Insufficient scopes |
| 404 | Resource not found |
| 409 | Conflict (e.g. cancel a completed run) |
| 429 | Rate limit exceeded |
| 500 | Internal server error |

## Starting the API Locally

```bash
dotnet run --project ./Fabricate.Api/Fabricate.Api.csproj
```

The API uses SQLite at `fabricate.db` in the working directory by default. To use PostgreSQL, set the connection string via environment variables or `appsettings.json`.

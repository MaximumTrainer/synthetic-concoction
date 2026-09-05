# Fabricate User Guide

## Table of Contents

1. [Introduction & Concepts](#introduction--concepts)
2. [Schema Discovery](#schema-discovery)
3. [Generation Plan](#generation-plan)
4. [Data Kinds](#data-kinds)
5. [Rules DSL](#rules-dsl)
6. [JSON Path Strategies](#json-path-strategies)
7. [Compliance Profiles](#compliance-profiles)
8. [Validation](#validation)
9. [Exports](#exports)
10. [Multi-Tenant Platform](#multi-tenant-platform)
11. [Agent Chat](#agent-chat)
12. [Workflows & Skills](#workflows--skills)
13. [API Keys](#api-keys)
14. [Self-Referencing FK Backfill](#self-referencing-fk-backfill)

---

## Introduction & Concepts

Fabricate is a deterministic synthetic data platform for .NET. Given a live database (SQLite or PostgreSQL), it:

1. **Discovers** the schema — tables, columns, primary keys, foreign keys, unique constraints, indexes.
2. **Plans** generation order using topological sort to respect FK dependencies.
3. **Generates** realistic synthetic rows using typed generators, optional rules, and compliance profiles.
4. **Validates** referential integrity, uniqueness, and nullability.
5. **Exports** results as CSV, JSON, SQL INSERT statements, and a run summary.

### Key Terms

| Term | Meaning |
|---|---|
| **Seed** | Integer that makes generation deterministic. Same seed + same schema = identical output. |
| **DataKind** | Classification of a column's data type (e.g. `Email`, `Integer`, `DateTime`). |
| **Rules DSL** | YAML or JSON configuration that overrides generation strategies per column. |
| **Compliance Profile** | Preset masking policy: `Default`, `Healthcare`, or `Finance`. |
| **Generation Plan** | Topologically ordered list of tables, tracking cycles and self-referencing tables. |
| **Validation Issue** | A detected constraint violation in generated data (FK mismatch, null in non-nullable column, etc.). |
| **Run Summary** | JSON artifact with table/row counts, timing, and validation issue count. |

---

## Schema Discovery

Fabricate uses provider-specific adapters to introspect a live database. The discovery process is read-only.

### Providers

| Provider | `--provider` value | Notes |
|---|---|---|
| SQLite | `sqlite` | Uses `Microsoft.Data.Sqlite`; reads `sqlite_master` and `pragma_*` views |
| PostgreSQL | `postgres` | Uses `Npgsql`; reads `information_schema` and `pg_catalog` |

### What is Discovered

For each table, Fabricate discovers:

- **Name** and **schema** (e.g. `public.users`)
- **Columns**: name, SQL type, inferred `DataKind`, nullability, max length, precision/scale, default expression, allowed values (CHECK enums)
- **Primary Key**: list of column names
- **Foreign Keys**: source table, source columns, referenced table, referenced columns
- **Unique Constraints**: column group name and members
- **Indexes**: name, columns, uniqueness flag

### CLI Command

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover \
  --provider sqlite \
  --connection "Data Source=./mydb.db" \
  --database mydb \
  --seed 42
```

Output is JSON printed to stdout:

```json
{
  "name": "mydb",
  "tables": [
    {
      "schema": "main",
      "name": "users",
      "qualifiedName": "main.users",
      "columns": [
        { "name": "id", "sqlType": "INTEGER", "dataKind": "Integer", "isNullable": false, "isPrimaryKey": true }
      ],
      "primaryKey": ["id"],
      "foreignKeys": [],
      "uniqueConstraints": [],
      "indexes": []
    }
  ]
}
```

### Schema Profile Command

`discover-profile` augments discovery with diagnostics: detected self-referencing tables, cycle edges, unmapped column types, and columns with no inferred `DataKind`.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover-profile \
  --provider sqlite \
  --connection "Data Source=./mydb.db"
```

Exit code 1 if any diagnostics are found; 0 otherwise.

---

## Generation Plan

Before generating data, Fabricate builds a **generation plan** by analysing FK edges.

### Topological Ordering

Tables are sorted so that parent tables (the referenced side of a FK) are generated before child tables (the referencing side). This ensures FK columns can reference rows that already exist.

### Cycle Detection

If the FK graph contains a cycle (e.g. `orders.customer_id → customers.id` and `customers.preferred_order_id → orders.id`), the cycle is recorded in `GenerationPlan.Cycles`. Fabricate generates one side first (breaking the cycle at an optional FK), then backfills the cyclic reference.

### Self-Referencing Tables

A table that has a FK pointing back to itself (e.g. `employees.manager_id → employees.id`) is listed in `GenerationPlan.SelfReferencingTables`. See [Self-Referencing FK Backfill](#self-referencing-fk-backfill) for generation semantics.

### Plan JSON Structure

```json
{
  "orderedTables": ["main.departments", "main.employees"],
  "cycles": [],
  "diagnostics": [],
  "selfReferencingTables": ["main.employees"]
}
```

---

## Data Kinds

Every column is mapped to a `DataKind` which determines how values are generated.

### Primitive Kinds

| DataKind | SQL Types Mapped | Example Output |
|---|---|---|
| `Boolean` | `BOOLEAN`, `BIT` | `true` |
| `Integer` | `INTEGER`, `INT`, `SMALLINT` | `42` |
| `Long` | `BIGINT` | `9876543210` |
| `Decimal` | `DECIMAL`, `NUMERIC` | `19.95` |
| `Double` | `REAL`, `FLOAT`, `DOUBLE` | `3.14159` |
| `String` | `TEXT`, `VARCHAR`, `CHAR` | `"Jqk9fPm"` |
| `Guid` | `UUID`, `UNIQUEIDENTIFIER` | `"3fa85f64-5717-4562-b3fc-2c963f66afa6"` |
| `Date` | `DATE` | `"2023-04-15"` |
| `DateTime` | `DATETIME`, `TIMESTAMP` | `"2023-04-15T09:30:00"` |
| `Json` | `JSON`, `JSONB` | `{}` |
| `Binary` | `BLOB`, `BYTEA`, `VARBINARY` | `"AAEC..."` (base64) |

### Semantic Kinds

| DataKind | Description | Example Output |
|---|---|---|
| `Email` | RFC-5321 email address | `"alice@example.com"` |
| `Phone` | E.164 phone number | `"+14155551234"` |
| `Name` | Full person name | `"Alice Johnson"` |
| `FirstName` | Given name | `"Alice"` |
| `LastName` | Family name | `"Johnson"` |
| `Address` | Street address | `"123 Main St"` |
| `PostalCode` | Postal/ZIP code | `"90210"` |
| `CountryCode` | ISO 3166-1 alpha-2 | `"US"` |
| `Url` | HTTP/HTTPS URL | `"https://example.com/path"` |
| `IpAddress` | IPv4 address | `"192.168.1.42"` |
| `Currency` | ISO 4217 currency code | `"USD"` |
| `CompanyName` | Organisation name | `"Acme Corp"` |
| `Text` | Long-form lorem ipsum text | `"Lorem ipsum dolor sit amet..."` |
| `Uuid` | UUID string (no dashes variant) | `"3fa85f6457174562b3fc2c963f66afa6"` |
| `TimestampTz` | ISO 8601 timestamp with timezone | `"2023-04-15T09:30:00+00:00"` |

### DataKind Inference

Fabricate infers `DataKind` from the SQL type name. Column names are used as secondary signals: a `VARCHAR` column named `email` is inferred as `DataKind.Email`.

To override inference, use a [Rules DSL](#rules-dsl) entry with the `strategy` field.

---

## Rules DSL

The Rules DSL lets you override every aspect of value generation on a per-column basis. Rules are stored in a YAML or JSON file (version `"1"`) and passed to the CLI with `--rules <path>`. See the dedicated [Rules DSL reference](how-to/rules-dsl.md) for a full field listing.

### How Rules Work

Without a rules file Fabricate infers a `DataKind` for each column from its SQL type and column name. Rules let you override that inference at any granularity:

- Force a specific **strategy** (any `DataKind` name) on a column regardless of its SQL type.
- Emit a **fixed value** for every row.
- Control what fraction of rows are **null**.
- Choose values from a **weighted distribution** of literals.
- Assign strategies to individual **paths inside a JSON column**.
- Shift the deterministic seed for a column to **decorrelate** related columns.

### CLI Usage

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider sqlite \
  --connection "Data Source=./mydb.db" \
  --rules ./rules.yaml \
  --rows 50
```

If the rules file fails validation, errors are printed to stderr and the process exits with code 1.

### File Format

YAML and JSON are interchangeable. Use whichever your team prefers.

```yaml
# YAML (rules.yaml)
version: "1"
tables:
  - table: "public.users"
    columns:
      - column: "email"
        strategy: "Email"
```

```json
// JSON (rules.json)
{
  "version": "1",
  "tables": [
    {
      "table": "public.users",
      "columns": [{ "column": "email", "strategy": "Email" }]
    }
  ]
}
```

The `table` name must be fully qualified with its schema, matching the value Fabricate uses in its schema discovery output: `"public.users"` for PostgreSQL or `"main.users"` for SQLite.

### Column Rule Fields

| Field | Type | Description |
|---|---|---|
| `column` | `string` | Column name. Required. |
| `strategy` | `string` | `DataKind` name to use (e.g. `"Email"`, `"Integer"`). Overrides inferred kind. |
| `fixedValue` | `any` | Emit this exact value before compliance masking is applied. Overrides generation-time strategy/distribution logic. |
| `nullRate` | `number [0,1]` | Fraction of rows emitting `null`. Only applies to nullable columns. |
| `seedOffset` | `integer` | Reserved for future deterministic-seed controls. Currently parsed/validated but not applied during generation. |
| `distribution` | `map<string,number>` | Weighted discrete value distribution. Weights need not sum to 1.0. |
| `jsonPaths` | `array` | Per-path strategies for JSON/JSONB columns. See [JSON Path Strategies](#json-path-strategies). |

### strategy — Override the Inferred Kind

```yaml
- column: "user_type"
  strategy: "String"        # force random string even if inferred as something else

- column: "profile_pic_url"
  strategy: "Url"           # emit a realistic URL on a TEXT column
```

### fixedValue — Constant for Every Row

```yaml
- column: "status"
  fixedValue: "active"

- column: "is_verified"
  fixedValue: true

- column: "score"
  fixedValue: 100
```

`fixedValue` has the highest precedence during generation — it overrides `strategy` and `distribution`.  
Compliance masking is applied afterward and may still transform the emitted value for sensitive columns.

### nullRate — Probabilistic Nulls

```yaml
- column: "middle_name"
  nullRate: 0.7         # 70% of rows will be null
```

Combine with `distribution`: `nullRate` is evaluated first, and non-null rows draw from the distribution.

```yaml
- column: "tier"
  nullRate: 0.1
  distribution:
    silver: 0.5
    gold:   0.3
    platinum: 0.2
# Result: 10% null, 45% silver, 27% gold, 18% platinum
```

### seedOffset — Current Status

`seedOffset` is accepted in the DSL but is not currently consumed by the generator pipeline.  
Use it as forward-compatible metadata for now; it does not change output in the current release.

```yaml
- column: "first_name"
  strategy: "FirstName"
  seedOffset: 1

- column: "last_name"
  strategy: "LastName"
  seedOffset: 2
```

### distribution — Weighted Literals

```yaml
- column: "country"
  distribution:
    US: 0.6
    GB: 0.2
    DE: 0.1
    FR: 0.1
```

Weights are normalised at generation time. Generation always samples from the `distribution` entries themselves (no fallback remainder path).

### Precedence Merge

`IRuleConfigurationService.Merge()` combines exactly three configuration layers in this order: **defaults < schema-derived < user**.  
For each table/column key, later layers replace the entire `ColumnRule` entry from earlier layers (not a per-field merge).  
Within an active column rule during generation, `fixedValue` has highest precedence.

### Complete Example

```yaml
version: "1"
tables:
  - table: "public.users"
    columns:
      - column: "email"
        strategy: "Email"
      - column: "name"
        strategy: "Name"
      - column: "status"
        fixedValue: "active"
      - column: "tier"
        nullRate: 0.1
        distribution:
          bronze: 0.5
          silver: 0.3
          gold: 0.2
      - column: "first_name"
        strategy: "FirstName"
        seedOffset: 1
      - column: "last_name"
        strategy: "LastName"
        seedOffset: 2
      - column: "preferences"
        jsonPaths:
          - path: "$.email"
            strategy: "Email"
          - path: "$.score"
            strategy: "Integer"
          - path: "$.optional"
            strategy: "String"
            nullRate: 0.5

  - table: "public.orders"
    columns:
      - column: "status"
        distribution:
          pending:    0.4
          processing: 0.3
          shipped:    0.2
          cancelled:  0.1
      - column: "notes"
        nullRate: 0.6
        strategy: "Text"
```

---

## JSON Path Strategies

For columns of type `JSON` or `JSONB`, you can assign generation strategies to individual paths within the document using `jsonPaths`.

### Notation

Paths use **dollar-dot notation**:

- `$.email` — top-level field
- `$.address.city` — nested field
- `$.a.b.c` — deeply nested field

Array indexing (`$.items[0]`) is not supported.

### Example Rules

```yaml
version: "1"
tables:
  - table: "public.users"
    columns:
      - column: "preferences"
        jsonPaths:
          - path: "$.email"
            strategy: "Email"
          - path: "$.address.city"
            strategy: "String"
          - path: "$.score"
            strategy: "Integer"
          - path: "$.tag"
            fixedValue: "synthetic"
          - path: "$.optional"
            strategy: "String"
            nullRate: 0.5
```

### Generated Output

```json
{
  "email": "alice@example.com",
  "address": { "city": "Maplewood" },
  "score": 7,
  "tag": "synthetic",
  "optional": null
}
```

Nested objects are constructed automatically from the dot-delimited path segments. If `nullRate` is set for a path, that path may be omitted from the document entirely.

---

## Compliance Profiles

Compliance profiles apply automatic masking to sensitive fields, reducing the risk of generating realistic PII in restricted environments.

### Default

No masking. All `DataKind` values generate realistic values.

### Healthcare (HIPAA-style)

Sensitive demographic and contact fields are masked or nulled:

| Field Category | Treatment |
|---|---|
| Email columns | Replaced with anonymised placeholder |
| Phone columns | Replaced with anonymised placeholder |
| Name / FirstName / LastName | Replaced with anonymised placeholder |
| Address / PostalCode | Replaced with anonymised placeholder |

### Finance (PCI-style)

Inherits Healthcare masking, plus additional financial field masking:

| Field Category | Treatment |
|---|---|
| All Healthcare fields | Masked as above |
| Currency/financial identifiers | Anonymised or zeroed |

### CLI Usage

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider postgres \
  --connection "Host=localhost;Database=prod_clone;Username=dev;Password=dev" \
  --compliance-profile Healthcare \
  --rows 1000
```

### Rules Override

Explicit rules entries (`strategy`, `fixedValue`) take precedence over the compliance profile. If you specify `strategy: "Email"` for a column that Healthcare would mask, the compliance profile masking still applies (compliance wins for masked categories). `fixedValue` always wins.

### ComplianceDecisions in Output

The `GenerationResult.ComplianceDecisions` list records every column affected by compliance masking, including:

- `Table` and `Column`
- `Strategy` (the applied `SensitiveFieldStrategy`)
- `Classification` (e.g. `"Email"`, `"Name"`)
- `Reason` (human-readable justification)
- `Source` (`ComplianceProfile` or `UserRule`)

---

## Validation

After generation, Fabricate runs a validation pass over the generated rows and records any issues in `GenerationResult.ValidationIssues`.

### Checks Performed

| Check | Description |
|---|---|
| **FK Integrity** | Every non-null FK value references a row in the parent table. |
| **Uniqueness** | Columns with `UNIQUE` constraints have no duplicate values. |
| **Nullability** | Non-nullable columns have no null values. |
| **Self-Ref Root** | Row 0 in a self-referencing table is valid (nullable FK = null; non-nullable FK emits a validation issue). |

### ValidationIssue Structure

```csharp
public sealed record ValidationIssue(string Table, string Column, string Reason);
```

Example:

```json
{ "table": "main.orders", "column": "customer_id", "reason": "FK value 999 not found in main.customers" }
```

### Exit Codes (CLI)

| Command | Code | Meaning |
|---|---|---|
| `validate` | 0 | No issues |
| `validate` | 3 | One or more validation issues |
| `generate` | 2 | One or more validation issues |

---

## Exports

The `generate` command writes three export formats plus a summary file to `--output` (default: `./artifacts`).

### Directory Layout

```
artifacts/
  csv/
    main.users.csv
    main.orders.csv
  json/
    main.users.json
    main.orders.json
  sql/
    main.users.sql
    main.orders.sql
  summary.json
```

### CSV

RFC-4180 compliant. One file per table. Header row is the column names. Null values are empty fields.

```csv
id,email,status
1,alice@example.com,active
2,bob@example.com,active
```

### JSON

One JSON array of row objects per file.

```json
[
  { "id": 1, "email": "alice@example.com", "status": "active" },
  { "id": 2, "email": "bob@example.com", "status": "active" }
]
```

### SQL

INSERT statements per table. Uses `NULL` for null values, `TRUE`/`FALSE` for booleans, proper SQL quoting for strings.

```sql
INSERT INTO "main"."users" ("id", "email", "status") VALUES (1, 'alice@example.com', 'active');
INSERT INTO "main"."users" ("id", "email", "status") VALUES (2, 'bob@example.com', 'active');
```

### summary.json

```json
{
  "startedAt": "2024-06-01T10:00:00Z",
  "completedAt": "2024-06-01T10:00:01Z",
  "tableCount": 3,
  "rowCount": 150,
  "validationIssueCount": 0,
  "messages": ["Generated 50 rows for main.users", "Generated 100 rows for main.orders"]
}
```

### Export Command (single format)

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- export \
  --provider sqlite \
  --connection "Data Source=./mydb.db" \
  --format csv \
  --output ./csv-only \
  --rows 20
```

---

## Multi-Tenant Platform

The REST API exposes a multi-tenant platform model built around Accounts, Workspaces, and Projects.

### Accounts

An **Account** is the top-level organisational unit. All users must belong to at least one account.

- Members have roles: `Member` or `Owner`.
- Invitation flow: an Owner sends an invitation by email with a time-limited token; the invitee accepts it via `POST /accounts/invitations/accept`.
- **Allowed Domains**: governance setting that restricts invitations to specific email domains.

### Workspaces

A **Workspace** is scoped under an Account. It contains:

- Connection catalog (database provider + connection string references)
- Secret references (resolved from environment variables at runtime)
- Agent instructions (system prompt / context for the chat agent)
- Members with RBAC roles: `Viewer`, `Editor`, `Admin`

Members can be added or removed via `POST /workspaces/{id}/members` and `DELETE /workspaces/{id}/members/{userId}`.

### Connections

A workspace connection points at a database the agent may introspect. The connection string is sent **once**, at
creation, encrypted with the same cipher as LLM credentials, and never returned.

```http
POST /workspaces/{workspaceId}/connections
{
  "name": "warehouse",
  "provider": "postgres",
  "connectionString": "Host=db.internal;Username=app;Password=…;Database=prod"
}
```

Supported providers: `sqlite`, `postgres` / `postgresql`. Anything else is a `400` naming the ones that work.

| Route | Purpose |
|---|---|
| `GET /workspaces/{id}/connections` | Summaries: fingerprint, redacted target, status, last validation. |
| `GET /workspaces/{id}/connections/{connectionId}` | One summary. Another workspace's id is `404`, never `403`. |
| `POST …/{connectionId}/rotate` | Replaces the connection string. The old one is not recoverable. |
| `POST …/{connectionId}/validate` | Opens the connection and reads metadata; reports reachability. |
| `DELETE …/{connectionId}` | Removes it. |

Every read returns a **summary**, never the connection string:

```json
{
  "id": "3fa85f64-…",
  "name": "warehouse",
  "provider": "postgres",
  "status": "active",
  "fingerprint": "9f3c2a1b4d5e",
  "redacted": "Host=db.internal;Username=***;Password=***;Database=prod",
  "hasSecret": true,
  "lastValidatedAt": "2026-09-05T12:00:00Z",
  "lastValidationError": null
}
```

The `redacted` form keeps the host and database so you can recognise which connection you are looking at, and
drops every credential. The `fingerprint` is a short hash — enough to tell two connections apart and to see that a
rotation happened, without being reversible.

Validation opens the connection and reads metadata rather than pinging, because a ping can succeed where the
credentials cannot actually read. A failure message is scrubbed before it is returned or logged: database drivers
quote the connection string back in their errors more often than not.

#### Which database a chat session sees

1. The session's **project database**, when the project has an external database naming a workspace connection.
2. The workspace's **single active connection**, when it has exactly one.
3. Otherwise the **instance-level** `SchemaProvider` configuration — which is what keeps the CLI and single-tenant
   self-hosting working exactly as before.

With several connections and no project binding, discovery falls back to the configured default rather than
guessing which of your databases to introspect.

### Projects

A **Project** is scoped under a Workspace. It holds:

- Database catalog entries
- Run history
- Soft-delete flag (`isArchived`)

Projects are soft-deleted via `DELETE /projects/{id}`.

### Governance

Account-level governance controls:

| Feature | Description |
|---|---|
| **Account Groups** | Logical groupings of members for access control. |
| **Allowed Domains** | Allowlist of email domains for invitations. |
| **Audit Log** | Append-only log of significant events (account/workspace/project changes, key operations). |

#### Reading the audit log

```http
GET /accounts/{accountId}/audit?page=1&pageSize=50&action=workspace
```

Open to any member of the account. Events come back newest first.

| Filter | Matches |
|---|---|
| `action` | Anywhere in the action name — `action=workspace` finds `workspace.created` and `workspace.access_granted`. |
| `actionPrefix` | The start of the action name, which selects a whole family: `actionPrefix=chat.` or `actionPrefix=api.`. |
| `apiKeyId` | Everything one API key did. |

#### What is recorded

| Action | When |
|---|---|
| `api.request` | Every authenticated request: which key called which **route template**, with method, status, scopes and duration. Anonymous endpoints (`/healthz`, Swagger) are not recorded. |
| `chat.tool_invoked` | A tool call ran and succeeded. |
| `chat.tool_failed` | A tool call ran and threw. |
| `chat.tool_blocked` | A tool call was refused because the tool is not in the workspace allowlist. |
| `chat.tool_requested` | A tool call was parked for review (`ReviewRequired` sessions). |
| `chat.tool_approved` | A parked call was approved; the run that follows is audited separately. |
| `chat.plan_stated` | The agent stated the steps it intends to take, before its first generating call. |
| `chat.plan_revised` | The agent revised a plan it had already stated. |

Two things are recorded deliberately narrowly:

- **Route templates, not paths.** A path carries workspace, project and session identifiers;
  `/workspaces/{workspaceId}/projects/{projectId}` says which endpoint was called without copying tenant
  identifiers into a log that is exported and kept for months. Headers, query values and bodies are never recorded.
- **Tool names, not payloads.** Tool arguments carry whatever the user or the model put in the prompt, and outputs
  carry query results. Copying either into the account audit log would make it a second, longer-lived copy of the
  conversation. The invocation id is recorded instead, so the payload stays reachable to anyone with the authority
  to read it.

`api.request` is on by default and can be sampled or switched off with `FABRICATE_API_USAGE_SAMPLING`; see
[the self-hosting guide](how-to/self-hosting.md#audit-retention).

#### Exporting the audit log

```http
GET /accounts/{accountId}/audit/export?from=2026-01-01T00:00:00Z&to=2026-09-01T00:00:00Z&format=json
```

**Account owners only** — members who can read the log through the query API cannot export it. The response is a
download (`Content-Disposition: attachment`), streamed rather than buffered, so exporting a large account does not
depend on it fitting in memory.

| Parameter | Default | Meaning |
|---|---|---|
| `from` | none | Only events at or after this instant. |
| `to` | none | Only events at or before this instant. |
| `format` | `json` | `json` for an array of events, `csv` for a header row plus one line per event. Anything else is a `400`. |

The export contains the same events the query API returns, oldest first. One difference: every event's `details`
field is **redacted on the way out**. The log is written by many call sites, so an export — a file that leaves the
building — does not assume all of them got redaction right. Values are replaced with `[redacted]` wherever the key
names something sensitive (`secret`, `password`, `token`, `apiKey`, `credential`, `fingerprint`, connection
strings), and known provider key shapes are stripped even when they appear without a key name. Credential
fingerprints are included deliberately: a fingerprint identifies a live key, and correlating one across accounts
says which tenants share it. Non-sensitive detail — provider names, model names, target ids — survives, or the
export would not be worth having.

Redaction applies to the export only. The stored event is left as written, because rewriting history in place to
hide a mistake is worse than the mistake.

Operators can cap how long events are kept; see
[Audit retention](how-to/self-hosting.md#audit-retention). Retention is off by default.

---

## Agent Chat

The chat system lets you interact with Fabricate in natural language via a persistent session. Each turn is answered
by the LLM the workspace is configured to use — either a credential the workspace registered itself
([bring your own key](how-to/byok-llm-credentials.md)) or, where allowed, the operator's platform credential
([self-hosting](how-to/self-hosting.md)). Fabricate ships with no model credential of its own.

### Sessions

A chat session is created with `POST /workspaces/{workspaceId}/chat/sessions` and has:

- `workspaceId` — the workspace it operates in
- `projectId` — optional; a project-bound LLM credential takes precedence when set
- `name` — display name
- `mode` — `Guided` (default), `Autonomous`, or `ReviewRequired`

Messages are posted to `POST /workspaces/{workspaceId}/chat/sessions/{id}/messages` (or `…/messages/stream` for
server-sent events). The response is the whole **turn**: your message, the assistant reply, every tool invocation
the model made, token usage, and a stop reason. Messages have a `role` of `User`, `Assistant`, `Tool` (a tool's
output) or `System` (a notice from Fabricate itself, such as a declined request or a missing credential).

### How a turn runs

1. Your message is persisted.
2. The system instructions are composed: Fabricate's base guidance, the mode guidance, then the workspace → project →
   session instruction layers.
3. The model is called with the recent history and the tools the workspace is allowed to use.
4. Tool calls the model makes are executed under **your** workspace permissions and their results fed back; this
   repeats until the model answers or `FABRICATE_LLM_MAX_TOOL_ITERATIONS` is reached.
5. The reply is persisted and returned.

A provider refusal or failure is recorded as a `System` notice, never an error response.

### Modes

| Mode | Tool calls |
| --- | --- |
| `Guided` | Run, and the model is instructed to explain before acting. |
| `Autonomous` | Run without confirmation. |
| `ReviewRequired` | Parked as `Pending`; an Editor or Admin approves each one via `POST …/tool-invocations/{id}/approve`. Use this wherever `generate_data` has real side effects. |

#### Asking rather than guessing

The agent is told, per mode, when to ask instead of assuming. A request like *"generate some test data"* against a
ten-table schema leaves the tables, the row counts and the compliance profile unstated, and guessing is the wrong
default for a tool that writes data.

| Mode | Behaviour when something is unspecified |
|---|---|
| `Guided` | Asks one specific question covering everything it needs, and makes no generating call until answered. A request that is already specific proceeds without asking. |
| `Autonomous` | Chooses a sensible default and proceeds, **stating every assumption** in the reply so it can be corrected. |
| `ReviewRequired` | Asks rather than parking a call the reviewer cannot judge, since a reviewer sees the call and not the reasoning. |

The cases that trigger a question are: unspecified tables or row counts on a multi-table schema, an unspecified
connection when the workspace has several, a compliance profile that would change what is produced, and anything
that would overwrite existing data.

#### Plans

For a request needing more than one tool call, the agent calls `state_plan` with the steps it intends to take
before the first call that generates or changes data, and calls it again with revised steps if it changes course.

A plan is a tool call rather than prose so it lands where every other call does: a tool invocation carrying the
steps, a message in the conversation, and an audit event — `chat.plan_stated` or `chat.plan_revised`. A revision
therefore sits next to the calls it governs, in order, instead of being buried in an assistant message. As with
every tool, the steps stay on the invocation and out of the audit log.

### Built-in Tools

The agent has two built-in tools. Each workspace can be restricted to a subset (the allowlist is enforced server-side;
a call to a tool outside it fails without executing).

#### `discover_schema`

Calls `ISchemaDiscoveryService` for the workspace's configured database and returns a JSON summary of discovered tables and columns.

Example prompt: *"Show me the schema for this database."*

#### `generate_data`

Runs the full generation pipeline (discover → plan → generate → validate) and returns a JSON result with row and table counts.

Example prompt: *"Generate 50 rows for each table."*

`/tool <name> <json>` invokes a tool directly, bypassing the model — useful for scripting and when no credential is configured.

### Instruction Context

Each workspace can store **agent instructions** — a system prompt that is prepended to every session in that workspace. This lets you customise the agent's behaviour per workspace (e.g. "Always use the Healthcare compliance profile for this workspace."). Project instructions and a per-session override layer on top, in that order.

### What is sent to the model

Schema metadata — table, column and type names, relationships — and tool outputs. **Row values are not sent**, and
that is enforced rather than assumed: every tool declares what class of content its result carries, and a tool
whose class the boundary forbids is not offered to the model at all.

| Content class | May be sent |
|---|---|
| `Metadata` — names, shapes, run summaries | Always |
| `AggregateStatistics` — histograms, distinct counts, min/max over real rows | Only with the workspace opt-in |
| `SampledValues` — values copied from real rows | Only with the workspace opt-in |

The opt-in is `allowSampledDataInPrompts` on the workspace LLM policy and defaults to false. It **cannot be
enabled on a `Healthcare` or `Finance` workspace**: the request is refused with `409` and the policy is left
unchanged. Every refusal is audited as `llm.boundary_blocked` with the tool name and content class, never the
payload. See [the self-hosting guide](how-to/self-hosting.md#the-prompt-data-boundary).

---

## LLM usage and token budgets

Every provider call is attributed to the workspace, project and session that caused it, and rolled up on demand.

```http
GET /workspaces/{workspaceId}/llm-usage?from=2026-09-01T00:00:00Z&to=2026-10-01T00:00:00Z&groupBy=model
GET /accounts/{accountId}/llm-usage?groupBy=credential
```

The workspace view is open to any workspace member — it is their own consumption, and hiding it from the people
doing the work is how a budget becomes a surprise. The account rollup spans workspaces the caller may not
individually belong to, so it is **account owners only**. Both default to the last 30 days.

| `groupBy` | Buckets by |
|---|---|
| `model` (default) | Model name. |
| `credential` | Credential id. Calls made on the operator's platform credential bucket under `platform`, so they are not misattributed to a tenant's key. |
| `day` | UTC date, `YYYY-MM-DD`. |

Each bucket carries `inputTokens`, `outputTokens`, `totalTokens`, `calls` and `failedCalls`. **Cost is not
returned** — prices change and differ by platform, so tokens are the unit.

One record is written per provider *attempt*, not per turn. A call that fails and is retried writes a row for each
try, flagged `RetriedFailure`, so a workspace whose calls keep failing is visible rather than silently slow.

### Budgets

`dailyTokenBudget` and `monthlyTokenBudget` on the workspace LLM policy cap consumption:

```http
PUT /workspaces/{workspaceId}/llm-credentials/policy
{
  "allowPlatformFallback": true,
  "dailyTokenBudget": 200000,
  "monthlyTokenBudget": 4000000
}
```

Omit a field to leave it unchanged; send `-1` to clear the cap. Workspace admins only.

Once a budget is reached the chat turn returns a `System` notice saying which budget was hit and when it resets,
**and no provider call is made** — a budget that only reports after the fact is not a budget. The daily budget
resets at 00:00 UTC and the monthly one at 00:00 UTC on the first of the month. When both are exceeded the daily
one is reported, because it is the more actionable message.

---

## Workflows & Skills

Workflows allow you to define multi-step automation sequences that can be triggered on demand.

### Creating a Workflow

```http
POST /workflows
{
  "workspaceId": "3fa85f64-...",
  "name": "Nightly Seed Refresh",
  "steps": [
    { "type": "generate", "rows": 500, "complianceProfile": "Default" },
    { "type": "export", "format": "sql" }
  ]
}
```

### Running a Workflow

```http
POST /workflows/{workflowId}/run
```

Returns `{ "runId": "..." }`. Use `GET /runs/{runId}` or the TypeScript SDK's `pollRun` to track completion.

### Skills

Fabricate has a custom skill registry. Skills are callable units registered in the platform (analogous to OpenAPI-defined functions). The skill registry supports OpenAPI contract ingestion — import an external API spec and Fabricate will expose its operations as callable workflow steps.

---

## Generated APIs

Ingest an OpenAPI contract, bind its operations to a generated dataset, and serve them — a mock API whose payloads
come from your own generated data and match the contract you gave it.

### POST /workspaces/{workspaceId}/api-contracts

```json
{ "name": "customers", "document": "{ \"openapi\": \"3.0.0\", ... }" }
```

Requires the workspace **Editor** role. Stores the contract and one endpoint per operation. A document that will
not parse is a `400` carrying the parser's own reasons.

### GET /workspaces/{workspaceId}/api-contracts
### GET /workspaces/{workspaceId}/api-endpoints

Each endpoint carries its `path`, `method`, `operationId`, the `responseKind` derived from the contract
(`Collection` for an array response, `Item` for a single object on a path ending in a parameter), its binding, and
`isServable`.

### PATCH /workspaces/{workspaceId}/api-endpoints/{endpointId}

```json
{ "artifactRunId": "3fa85f64-...", "boundTable": "main.customers" }
```

Binds the endpoint to a table in a completed run, exported with the `json` format. Send `isActive` to toggle it,
or `clearBinding: true` to unbind.

The bound rows are checked against the contract's response schema **at bind time**, and any mismatch is stored on
the endpoint as `diagnostics` — finding out when a client rejects the payload is the worst moment to learn it. An
endpoint with a diagnostic is stored but not served, so a bad binding is visible rather than fatal.

The check is deliberately narrow: required properties present, and declared primitive types not contradicted. That
catches the mistake it exists for — binding to the wrong table — without a full JSON Schema implementation whose
failures would be harder to act on than the mismatch.

### GET /workspaces/{workspaceId}/mock/{path}

```http
GET /workspaces/{workspaceId}/mock/customers
GET /workspaces/{workspaceId}/mock/customers/42
```

Matches the path against the contract's templates, preferring literal segments over parameters, and returns the
bound rows: an array for a `Collection` operation, one row for an `Item` operation matched on the trailing path
parameter. The `X-Fabricate-Operation` response header names the operation that answered.

Everything that is not a live, bound, servable endpoint is a `404`: an unbound endpoint, an inactive one, an
endpoint with diagnostics, a path outside the contract, a method the contract does not declare for that path, and
an item id with no matching row.

Mock routes use the same API-key authentication and rate limiting as the rest of the API, and each call is audited
like any other request — a mock endpoint is still this instance serving a tenant's data.

---

## API Keys

API keys authenticate requests to the REST API.

### Format

All keys start with the prefix `cnc_`. The plaintext secret is shown only once at creation time. The platform stores only the SHA-256 hash of the key.

```
cnc_s3cretv4lueh3r3...
```

### Scopes

| Scope | Access |
|---|---|
| `workspace:read` | Read-only access to workspace resources |
| `workspace:write` | Read + write access to workspace resources |
| `admin` | Full administrative access |

### Creating a Key

```http
POST /accounts/{accountId}/api-keys
{
  "name": "ci-pipeline",
  "scopes": ["workspace:read", "workspace:write"],
  "expiry": "90.00:00:00"
}
```

Response includes the `plaintextSecret` (shown once):

```json
{
  "id": "3fa85f64-...",
  "name": "ci-pipeline",
  "plaintextSecret": "cnc_abc123...",
  "scopes": ["workspace:read", "workspace:write"],
  "expiresAt": "2024-09-01T00:00:00Z"
}
```

### Revoking a Key

```http
DELETE /accounts/{accountId}/api-keys/{keyId}
```

The key is immediately invalid. Revoked keys are kept in the database for audit purposes.

### Listing Keys

```http
GET /accounts/{accountId}/api-keys
```

Returns metadata only (no secrets).

---

## Self-Referencing FK Backfill

A **self-referencing foreign key** is a column in table T that references the primary key of the same table T. A common example is `employees.manager_id → employees.id`.

### Generation Strategy

Fabricate handles self-referencing tables with the following algorithm:

1. **Row 0 (root)**: The first generated row sets the self-referencing FK to `null` (if the column is nullable).
2. **Rows 1..N**: Each subsequent row references the row at index `N-1` (forming a chain/tree).

This ensures every non-root row has a valid parent within the same generated batch.

### Non-Nullable Self-Ref FK

If the self-referencing FK column is **not nullable**, row 0 cannot set it to `null`. This situation is recorded as a `ValidationIssue`:

```json
{
  "table": "main.employees",
  "column": "manager_id",
  "reason": "Self-referencing FK manager_id is non-nullable; root row cannot reference itself without a pre-existing row."
}
```

The `GenerationPlan.SelfReferencingTables` list flags these tables so you can identify them early with `discover-profile`.

### Workaround

Use a rules file to set a `fixedValue` for the FK column on row 0, or restructure the schema to allow nulls on the root manager.

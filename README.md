# Fabricate

![Build](https://img.shields.io/badge/build-passing-brightgreen) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![License](https://img.shields.io/badge/license-MIT-blue)

Fabricate is a .NET 10 synthetic data platform that discovers your database schema, generates realistic relational data with full referential integrity, and exports it in CSV, JSON, and SQL formats. It ships as a CLI, a REST API, and a TypeScript SDK, and supports a deterministic seeding model so the same seed always produces the same dataset.

### How it works

You can drive the same pipeline through three entry points: the CLI (`discover`, `discover-profile`, `generate`, `validate`, and `export`) for local workflows, the REST API for managed runs, and the TypeScript SDK as a typed client over that API. Regardless of entry point, Fabricate follows the same discovery → plan/generate → export flow.

**Schema discovery.** Fabricate connects to a live SQLite or PostgreSQL database and performs a read-only introspection using provider-specific adapters — `sqlite_master` and `pragma_*` views for SQLite; `information_schema` queries for PostgreSQL. For every table it captures column names, SQL types, inferred data kinds (e.g. `Email`, `Integer`, `Guid`), nullability, primary keys, foreign keys, and unique constraints, plus SQLite index metadata. The `discover` command prints this as JSON; `discover-profile` augments it with diagnostics such as self-referencing tables, cycle edges, and unmapped column types.

**Relational data generation.** Before producing any rows, Fabricate analyses the foreign-key graph and builds a *generation plan*: tables are topologically sorted so parent tables are always populated before their dependents. That plan becomes the working data graph for generation — parent primary keys are collected first, then child tables draw FK values from those already-materialized parent rows so relationships stay consistent. Cycles in the FK graph are detected and broken at an optional FK column, with the cyclic reference backfilled after both sides exist. Self-referencing tables (e.g. `employees.manager_id → employees.id`) are handled with a chain strategy — row 0 gets a null root and each subsequent row references the previous one. All values are produced deterministically from a single integer seed, so the same seed and schema always produce the same dataset. Generation strategies are controlled by inferred data kinds, an optional YAML/JSON Rules DSL (per-column strategy, fixed value, null rate, weighted distribution, JSON-path rules), and a compliance profile (`Default`, `Healthcare`, or `Finance`) that applies masking for sensitive field categories. After generation, Fabricate records structured validation issues for rule/constraint checks such as non-nullability, string length, allowed values, uniqueness, and any FK backfill cases it could not safely resolve.

**Export.** The `generate` command writes all three formats to a single output directory, organizing the results by exporter (`csv/`, `json/`, and `sql/`) plus a root `summary.json` artifact. CSV output is RFC-4180 compliant with one file per table and nulls as empty fields; JSON output is one array of row objects per table; SQL output is standard `INSERT` statements with proper quoting, `NULL` literals, and `TRUE`/`FALSE` booleans. The `export` command runs the same generation pipeline but targets a single format when only one artifact set is needed.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQLite or PostgreSQL (for schema discovery)
- Node.js 20+ (optional, for the TypeScript SDK only)

## Clone & Build

```bash
git clone https://github.com/MaximumTrainer/synthetic-fabricate.git
cd synthetic-fabricate
dotnet build Fabricate.slnx
```

## Run Tests

```bash
dotnet test Fabricate.slnx
```

81 tests (xUnit + FluentAssertions), all passing.

## CLI Quick Start

All CLI commands are invoked via `dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj --`.

### discover

Prints the discovered schema as JSON.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --database mydb \
  --seed 42
```

### discover-profile

Profiles the schema and prints diagnostic JSON (self-refs, cycles, unmapped columns).

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover-profile \
  --provider sqlite \
  --connection "Data Source=./sample.db"
```

### generate

Discovers schema, generates synthetic rows, and writes CSV + JSON + SQL + `summary.json` to `--output`.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --seed 42 \
  --rows 100 \
  --rules ./rules.yaml \
  --compliance-profile Default \
  --output ./artifacts
```

### validate

Generates data and prints a validation summary; exits with code 3 if issues are found.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- validate \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --rows 50
```

### export

Generates data and exports a single format to `--output`.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- export \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --format csv \
  --output ./csv-only
```

Supported `--format` values: `json`, `csv`, `sql`.

## Running the REST API

```bash
dotnet run --project ./Fabricate.Api/Fabricate.Api.csproj
```

The API starts on `http://localhost:5000` by default. Swagger UI is available at `http://localhost:5000/swagger`.

All endpoints require the header `X-Api-Key: cnc_<secret>`. See [docs/how-to/rest-api.md](docs/how-to/rest-api.md) for authentication setup.

### Self-hosting and bringing your own LLM key

Fabricate ships with **no embedded credentials**. You supply your own LLM key (Anthropic, any OpenAI-compatible
endpoint, or Claude via Bedrock / Vertex AI / Foundry using cloud identity), your own PostgreSQL, and your own
bootstrap API key. The quickest working instance is one command:

```bash
cp .env.example .env        # fill in FABRICATE__BootstrapApiKey and your LLM key
docker compose up --build   # API + PostgreSQL; schema is migrated on first boot
```

- [Self-hosting guide](docs/how-to/self-hosting.md) — configuration contract, Fly.io reference deployment
  (`fly.toml` + deploy-from-GitHub workflow), Render/Railway alternatives, egress profile, cost.
- [Bring your own LLM key](docs/how-to/byok-llm-credentials.md) — per-workspace credentials, encrypted at rest,
  with rotation, revocation, validation and an egress allowlist for tenant-supplied endpoints.
- [CI integration secrets](docs/how-to/ci-integration-secrets.md) — opt-in tests against real databases and clouds.

## Solution Structure

| Project | Purpose |
|---|---|
| `Fabricate.Domain` | Entities, value objects, enums, domain models (including provider-neutral LLM and credential models) |
| `Fabricate.Application` | Use cases, port interfaces, orchestration; the agent chat loop and BYOK credential lifecycle |
| `Fabricate.Infrastructure` | Adapters: SQLite/PostgreSQL providers, EF Core persistence (SQLite + PostgreSQL), CSV/JSON/SQL/Parquet exporters, LLM adapters (Anthropic SDK incl. Bedrock/Vertex/Foundry, OpenAI-compatible), Data Protection secret cipher |
| `Fabricate.Cli` | `System.CommandLine` CLI (5 commands) |
| `Fabricate.Api` | ASP.NET Core Minimal API (REST, SSE chat streaming, Swagger) |
| `Fabricate.Tests` | 253 xUnit + FluentAssertions tests (unit, SQLite and PostgreSQL/Testcontainers integration) |
| `sdk/typescript/` | `@fabricate/client` npm package (CJS + ESM + DTS) |

## Extending Fabricate

### Register a Custom Generator

Implement `IDataGenerator` in `Fabricate.Application`, register it in the DI container via `ServiceCollectionExtensions`, and map your new `DataKind` values to it.

### Custom Rules

Write a `rules.yaml` file targeting specific tables and columns:

```yaml
version: "1"
tables:
  - table: "public.users"
    columns:
      - column: "email"
        strategy: "Email"
      - column: "status"
        fixedValue: "active"
```

Pass it with `--rules ./rules.yaml` on any command that supports it. See [docs/how-to/rules-dsl.md](docs/how-to/rules-dsl.md) for the full reference.

## Contributing

Pull requests are welcome. All contributions must:

1. Follow **Red → Green → Refactor** TDD.
2. Respect **hexagonal architecture** — no infrastructure imports in Domain or Application.
3. Pass `dotnet test Fabricate.slnx` with no new failures.
4. Keep new public APIs covered by tests.

See [docs/user-guide.md](docs/user-guide.md) for full platform documentation.

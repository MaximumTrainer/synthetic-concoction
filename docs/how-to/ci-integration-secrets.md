# CI secrets for integration tests

Most of Fabricate's integration tests need no secret at all. The database adapters run against **emulators and
containers** in the main [`ci`](../../.github/workflows/ci.yml) job — PostgreSQL, MongoDB, DynamoDB Local, the
Firestore emulator, MinIO, Azurite and fake-gcs-server — so they run on every push, and on a laptop with Docker,
without anyone holding a cloud account.

What is left needs either an account nobody can emulate or money per run, and lives in the
[`integration-tests`](../../.github/workflows/integration-tests.yml) workflow: it runs weekly and on manual
dispatch, never on pull requests from forks, because secrets are not available there.

Every gated test **states whether it was exercised** in its output. A suite that quietly does nothing is the
failure this arrangement exists to avoid, so absence is reported rather than inferred from a green run.

## What runs with no credentials

| Suite | What it needs |
| --- | --- |
| `NoSqlProfilerTests` (MongoDB) | Docker — `mongo:7` |
| `NoSqlEmulatorTests` (DynamoDB) | Docker — `amazon/dynamodb-local` |
| `NoSqlEmulatorTests` (Firestore) | Docker — the Google Cloud CLI emulators image |
| `CloudArtifactStoreTests` (Azure Blob, GCS) | Docker — Azurite and fake-gcs-server |
| `S3ArtifactStoreTests` | Docker — MinIO |
| `PostgresPersistenceIntegrationTests` | Docker — `postgres:16` |

Set `FABRICATE_SKIP_DOCKER_TESTS=1` to skip all of them where no Docker daemon is available. Without that
variable, each one reports that it could not start rather than passing silently.

## PostgreSQL (schema discovery)

`PostgreSqlSchemaProviderTests` reads `FABRICATE_POSTGRES_TEST_CONNECTION`. The workflow starts a `postgres:16`
service container and points the variable at it; the same variable enables the suite locally:

```bash
export FABRICATE_POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=fabricate_test"
dotnet test --filter "FullyQualifiedName~PostgreSql"
```

## Azure Cosmos DB (issue #53)

Cosmos has an emulator, but it is a multi-gigabyte image that takes minutes to become healthy, so it is opt-in
rather than part of every push:

```bash
export FABRICATE_COSMOS_EMULATOR=1
dotnet test --filter "FullyQualifiedName~NoSqlEmulatorTests"
```

The weekly workflow sets it. With the variable unset, the Cosmos tests print
`Cosmos DB NOT exercised` and the suite's coverage report names it as not run.

Two connection-string keys exist for reaching an emulator, and both are refused where they would weaken a real
connection: `ConnectionMode=Gateway` (also the mode to use from behind a restrictive egress policy) and
`DisableServerCertificateValidation=True`, which is accepted **only** for a loopback endpoint.

No account is needed for CI. If you want to point the suite at a real account instead, supply a full connection
string in place of the emulator's.

## The clarifying-question eval (issues #87, #91)

Whether the agent asks before acting is the model's judgement, so it can only be checked against a real model.

| Secret / variable | Description |
| --- | --- |
| `FABRICATE_LIVE_LLM_API_KEY` | An Anthropic API key. `ANTHROPIC_API_KEY` is also read, for a local run. |
| `FABRICATE_LIVE_LLM_MODEL` | *(optional, a repository variable)* Model id; defaults to `claude-opus-5`. |

It costs one API call per fixture — seven at present — and reports a **pass rate** rather than asserting each
fixture, because a model's reply is not deterministic and a suite that fails on one borderline prompt would be
switched off within a week. It fails when the rate falls below 6/7, which is a regression in the guidance rather
than a coin landing the other way up.

```bash
export FABRICATE_LIVE_LLM_API_KEY="sk-ant-…"
dotnet test --filter "FullyQualifiedName~AgentClarificationLiveEvalTests" --logger "console;verbosity=detailed"
```

Without the key the test prints that behaviour was **not** verified and points at
`AgentClarificationEvalTests`, which covers the prompt contract and the harness offline.

## Which gates which

| Test class | Gate |
| --- | --- |
| `PostgreSqlSchemaProviderTests` | `FABRICATE_POSTGRES_TEST_CONNECTION` |
| `NoSqlEmulatorTests` (DynamoDB, Firestore) | Docker daemon reachable |
| `NoSqlEmulatorTests` (Cosmos DB) | `FABRICATE_COSMOS_EMULATOR=1` + Docker |
| `NoSqlProfilerTests` (MongoDB) | Docker daemon reachable |
| `AgentClarificationLiveEvalTests` | `FABRICATE_LIVE_LLM_API_KEY` or `ANTHROPIC_API_KEY` |

A secret that is present but wrong produces a **failing** test, not a skipped one, so misconfiguration is visible.

## Hygiene

- Never print secrets in workflow logs; the workflow only echoes which gates are enabled.
- Rotate keys on a schedule and after any contributor with access leaves.
- The Cosmos emulator key in the test fixture is Microsoft's published, non-secret emulator key. It is not a
  credential and grants access to nothing outside the container.

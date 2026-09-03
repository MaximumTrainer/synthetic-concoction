# CI secrets for integration tests

Fabricate's integration tests against real databases and cloud services are **opt-in**: each test checks for its
environment variable and returns immediately when it is absent, so `dotnet test` stays green on a laptop with no
credentials. The [`integration-tests`](../../.github/workflows/integration-tests.yml) workflow runs them in CI when the
corresponding GitHub Actions secrets are configured. It runs on a weekly schedule and on manual dispatch; it never runs
on pull requests from forks, because secrets are not available there.

Configure secrets under **Settings → Secrets and variables → Actions** in the repository (or in a GitHub *environment*
named `integration`, which the workflow targets so that approvals and secret scoping apply).

## PostgreSQL (schema discovery and application persistence)

No secret is needed. The workflow starts a `postgres:16` service container and sets
`FABRICATE_POSTGRES_TEST_CONNECTION` to it. The same variable enables the tests locally:

```bash
export FABRICATE_POSTGRES_TEST_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=fabricate_test"
dotnet test --filter "FullyQualifiedName~PostgreSql"
```

The `Fabricate.Tests.Integration.PostgresPersistenceIntegrationTests` suite instead uses
[Testcontainers](https://dotnet.testcontainers.org/) and needs only a reachable Docker daemon; it skips itself when
there is none.

## Azure Cosmos DB (issue #53)

| Secret | Description |
| --- | --- |
| `COSMOSDB_CONNECTION_STRING` | Full connection string: `AccountEndpoint=https://…;AccountKey=…` |
| `COSMOSDB_TEST_DATABASE` | *(optional)* Database name; defaults to `fabricate_test` |

**Where to get it:** Azure Portal → your Cosmos DB account → **Keys** → *Primary Connection String*. Prefer a
read-only key if the tests only discover schema; the discoverer samples documents but never writes.

## MongoDB (issue #54)

| Secret | Description |
| --- | --- |
| `MONGODB_CONNECTION_STRING` | e.g. `mongodb+srv://user:pass@cluster.mongodb.net` |
| `MONGODB_TEST_DATABASE` | *(optional)* Database name; defaults to `fabricate_test` |

Create a dedicated database user with the `read` role on the test database only.

## AWS DynamoDB (issue #55)

| Secret | Description |
| --- | --- |
| `AWS_DEFAULT_REGION` | e.g. `us-east-1` |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` | IAM credentials **or** leave unset and use OIDC (below) |
| `DYNAMODB_TABLE_PREFIX` | *(optional)* Restricts discovery to tables with this prefix |
| `DYNAMODB_LOCAL_URL` | *(optional)* Local endpoint, e.g. `http://localhost:8000`, for DynamoDB Local |

**IAM permissions:** `dynamodb:ListTables`, `dynamodb:DescribeTable`, `dynamodb:Scan` — scoped to the test tables.

**Recommended: OIDC instead of long-lived keys.** Create an IAM role trusted by GitHub's OIDC provider for this
repository and set `AWS_ROLE_TO_ASSUME` (a repository *variable*, not a secret). The workflow uses
`aws-actions/configure-aws-credentials` with `role-to-assume` when that variable is present and falls back to the
static keys otherwise.

## GCP Firestore (issue #56)

| Secret | Description |
| --- | --- |
| `GOOGLE_CLOUD_PROJECT` | GCP project id |
| `GOOGLE_APPLICATION_CREDENTIALS_JSON` | The **contents** of a service-account key JSON file |

**Setup:**

1. Create a service account with the *Cloud Datastore User* role (`roles/datastore.user`) on the project.
2. Create a JSON key for it.
3. Paste the file contents into the `GOOGLE_APPLICATION_CREDENTIALS_JSON` secret.
4. The workflow writes the secret to a temporary file and exports `GOOGLE_APPLICATION_CREDENTIALS` pointing at it —
   the Google client libraries read the path, not the contents:

```yaml
- name: Configure GCP credentials
  if: env.GOOGLE_APPLICATION_CREDENTIALS_JSON != ''
  env:
    GOOGLE_APPLICATION_CREDENTIALS_JSON: ${{ secrets.GOOGLE_APPLICATION_CREDENTIALS_JSON }}
  run: |
    echo "$GOOGLE_APPLICATION_CREDENTIALS_JSON" > "$RUNNER_TEMP/gcp-credentials.json"
    echo "GOOGLE_APPLICATION_CREDENTIALS=$RUNNER_TEMP/gcp-credentials.json" >> "$GITHUB_ENV"
```

Workload Identity Federation (`google-github-actions/auth` with `workload_identity_provider`) is the keyless
alternative and is preferred where the project allows it.

## Which secrets gate which tests

| Test class | Gate |
| --- | --- |
| `PostgreSqlSchemaProviderTests` | `FABRICATE_POSTGRES_TEST_CONNECTION` |
| `PostgresPersistenceIntegrationTests` | Docker daemon reachable |
| `NoSqlAdapterIntegrationTests` (Cosmos) | `COSMOSDB_CONNECTION_STRING` |
| `NoSqlAdapterIntegrationTests` (Mongo) | `MONGODB_CONNECTION_STRING` |
| `NoSqlAdapterIntegrationTests` (DynamoDB) | `AWS_DEFAULT_REGION` (+ IAM credentials or `DYNAMODB_LOCAL_URL`) |
| `NoSqlAdapterIntegrationTests` (Firestore) | `GOOGLE_CLOUD_PROJECT` + `GOOGLE_APPLICATION_CREDENTIALS` |

A secret that is present but wrong produces a **failing** test, not a skipped one, so misconfiguration is visible.

## Hygiene

- Never print secrets in workflow logs; the workflow only echoes which gates are enabled.
- Rotate keys on a schedule and after any contributor with access leaves.
- Use test-only databases and accounts; the discoverers sample real documents to infer field types.

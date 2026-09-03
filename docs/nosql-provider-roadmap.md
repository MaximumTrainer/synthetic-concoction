# NoSQL Provider Roadmap

Fabricate currently supports **SQLite** and **PostgreSQL** for schema discovery and data profiling. This document defines the roadmap for extending support to cloud-native and document database platforms.

---

## Supported Provider Roadmap

| Provider | Platform | Status | Issue |
|----------|----------|--------|-------|
| SQLite | - | ✅ Implemented | — |
| PostgreSQL | - | ✅ Implemented | — |
| Azure Cosmos DB | Azure | 🚧 Stub — design complete | [#53](https://github.com/MaximumTrainer/synthetic-fabricate/issues/53) |
| MongoDB | Atlas / self-hosted | 🚧 Stub — design complete | [#54](https://github.com/MaximumTrainer/synthetic-fabricate/issues/54) |
| AWS DynamoDB | AWS | 🚧 Stub — design complete | [#55](https://github.com/MaximumTrainer/synthetic-fabricate/issues/55) |
| GCP Firestore | GCP | 🚧 Stub — design complete | [#56](https://github.com/MaximumTrainer/synthetic-fabricate/issues/56) |
| GCP Spanner | GCP | 📋 Planned | — |
| Azure Table Storage | Azure | 📋 Planned | — |

---

## Canonical Metadata Model

Fabricate represents any NoSQL collection using the provider-neutral `CollectionMetadata` record.

### Relational vs NoSQL metadata mapping

| Relational concept | NoSQL equivalent | Notes |
|-------------------|-----------------|-------|
| Table | Collection | May also be "container" (Cosmos DB) |
| Column | Field | Fields are nested; types are inferred by sampling |
| Primary key | Partition key + sort key | Composite in DynamoDB; `/id` in Cosmos DB |
| Foreign key | Reference hint | NoSQL has no enforced FK — only application-level references |
| Unique constraint | Unique index | Provider-specific support varies |
| Schema | Database / Namespace | Some providers use "namespace" or "account" instead |

### Document field types

`DocumentFieldType` captures the superset of types found across providers:

| Value | Description |
|-------|-------------|
| `String` | UTF-8 text |
| `Number` | Integer or floating-point (JSON-unified) |
| `Boolean` | True / false |
| `Object` | Nested document / sub-object |
| `Array` | Ordered list of values (may be mixed type) |
| `Null` | Explicit null / absent field |
| `Binary` | Byte array (BSON Binary, Cosmos DB Buffer) |
| `Date` | ISO-8601 or provider-specific date-time type |
| `ObjectId` | MongoDB BSON ObjectId |
| `Unknown` | Observed in fewer than 1% of sampled documents |

### Metadata gaps for NoSQL providers

The following relational concepts have **no direct NoSQL equivalent** and require provider-specific handling:

| Gap | Mitigation in Fabricate |
|-----|------------------------|
| Enforced referential integrity | `RelationshipHint` annotations (future — not yet modelled) |
| Fixed column schema | Field inference by sampling N documents; confidence score recorded |
| SQL data types (VARCHAR, INT…) | `DocumentFieldType` enum replaces `DataKind` for NoSQL |
| Check constraints | No equivalent — application-level validation only |
| Composite unique constraints | Modelled as `CollectionIndexDescriptor` with `IsUnique = true` |
| NULL vs absent field | Both mapped to `IsNullable = true` |

---

## Adapter Design

### Hexagonal architecture placement

```
Fabricate.Application.Abstractions
  INoSqlSchemaDiscoverer       ← port interface
  INoSqlDataProfiler           ← port interface
  INoSqlSchemaDiscovererFactory
  INoSqlDataProfilerFactory

Fabricate.Infrastructure.Schema
  CosmosDbSchemaDiscoverer     ← adapter (stub → full)
  MongoDbSchemaDiscoverer      ← adapter (stub → full)
  DynamoDbSchemaDiscoverer     ← adapter (stub → full)
  FirestoreSchemaDiscoverer    ← adapter (stub → full)
  NoSqlSchemaDiscovererFactory ← registered in DI
```

### Discoverer contract

```csharp
public interface INoSqlSchemaDiscoverer
{
    string ProviderName { get; }

    Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default);
}
```

### Profiler contract

```csharp
public interface INoSqlDataProfiler
{
    string ProviderName { get; }

    Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default);
}
```

### Field inference approach

Each full adapter implementation should:

1. **List collections** — enumerate all collections/containers/tables in the target database.
2. **Sample documents** — read a representative sample (e.g. 200–1000 documents) per collection using a provider-appropriate scan or query. **Never read the full dataset.**
3. **Infer field types** — union field names across all sampled documents; use the most-frequent non-null type as `FieldType`; set `IsNullable = true` if any sampled document omits or nulls the field.
4. **Infer nested fields** — recursively apply the above for any field typed as `Object`.
5. **Capture partition key** — extract from the collection descriptor, not from document sampling.

---

## Provider-Specific Auth & Connection Requirements

### Azure Cosmos DB

| Requirement | Detail |
|-------------|--------|
| NuGet package | `Microsoft.Azure.Cosmos` |
| Connection string | `AccountEndpoint=https://<account>.documents.azure.com:443/;AccountKey=<key>` |
| Preferred auth | Managed Identity (`DefaultAzureCredential`) — no key required |
| Required permissions | `Cosmos DB Built-in Data Reader` role (read-only) |
| Connection string env var | `COSMOSDB_CONNECTION_STRING` or `COSMOSDB_ENDPOINT` + `COSMOSDB_KEY` |
| Never hardcode | Keys must be read from environment variables or Azure Key Vault |

### MongoDB

| Requirement | Detail |
|-------------|--------|
| NuGet package | `MongoDB.Driver` |
| Connection string | `mongodb://user:pass@host:27017/db` or Atlas SRV: `mongodb+srv://...` |
| Preferred auth | SCRAM-SHA-256 or X.509 cert |
| Required permissions | `read` role on target database (minimum) |
| Connection string env var | `MONGODB_CONNECTION_STRING` |
| Never hardcode | Credentials must be read from environment variables or a secrets manager |

### AWS DynamoDB

| Requirement | Detail |
|-------------|--------|
| NuGet package | `AWSSDK.DynamoDBv2` |
| Auth | IAM role (preferred) — no access key needed on EC2/ECS/Lambda |
| Fallback auth | `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY` + `AWS_DEFAULT_REGION` |
| Required IAM permissions | `dynamodb:ListTables`, `dynamodb:DescribeTable`, `dynamodb:Scan` (with `Limit` to avoid full scans) |
| Connection string | AWS region string, e.g. `eu-west-1` |
| Never hardcode | Never hardcode AWS credentials — use IAM roles or `~/.aws/credentials` |

### GCP Firestore

| Requirement | Detail |
|-------------|--------|
| NuGet package | `Google.Cloud.Firestore` |
| Auth | Application Default Credentials (ADC) via `GOOGLE_APPLICATION_CREDENTIALS` path or Workload Identity on GKE |
| Required IAM roles | `roles/datastore.viewer` (Firestore in Datastore mode) or `roles/firebase.viewer` |
| Connection string | GCP project ID, e.g. `my-project-12345` |
| Never hardcode | Never hardcode service account keys — use ADC or Workload Identity |

---

## Test Strategy

### Unit tests (no cloud dependency)

- ✅ Stub adapters throw `NotSupportedException` with a descriptive message and the correct issue URL.
- ✅ `NoSqlSchemaDiscovererFactory` resolves registered providers by name (case-insensitive).
- ✅ `NoSqlSchemaDiscovererFactory` throws `NotSupportedException` for unknown providers.
- ✅ `CollectionMetadata`, `FieldDescriptor`, `PartitionKeyDescriptor` value-object construction.

### Integration tests (per-provider, cloud sandbox required)

Each full adapter (#53–#56) must include integration tests that:

- Connect to a real (dev/sandbox) instance of the provider.
- Discover collections from a known fixture database.
- Assert that expected fields and types are returned.
- Assert that no raw document content is returned (only metadata).
- Use environment-variable-gated skipping (`Skip.If(string.IsNullOrEmpty(connectionString))`).

### Security tests

- Confirm that no connection strings, keys, or document values appear in log output.
- Confirm that sampling is bounded (e.g. a `maxSampleSize` parameter prevents runaway scans).

---

## Follow-up Implementation Issues

| Issue | Provider | Work |
|-------|----------|------|
| [#53](https://github.com/MaximumTrainer/synthetic-fabricate/issues/53) | Azure Cosmos DB | Full `CosmosDbSchemaDiscoverer` + profiler |
| [#54](https://github.com/MaximumTrainer/synthetic-fabricate/issues/54) | MongoDB | Full `MongoDbSchemaDiscoverer` + profiler |
| [#55](https://github.com/MaximumTrainer/synthetic-fabricate/issues/55) | AWS DynamoDB | Full `DynamoDbSchemaDiscoverer` + profiler |
| [#56](https://github.com/MaximumTrainer/synthetic-fabricate/issues/56) | GCP Firestore | Full `FirestoreSchemaDiscoverer` + profiler |

# NoSQL Provider Roadmap

Fabricate currently supports **SQLite** and **PostgreSQL** for schema discovery and data profiling. This document defines the roadmap for extending support to cloud-native and document database platforms.

---

## Supported Provider Roadmap

| Provider | Platform | Status | Issue |
|----------|----------|--------|-------|
| SQLite | - | ✅ Implemented | — |
| PostgreSQL | - | ✅ Implemented | — |
| Azure Cosmos DB | Azure | ✅ Implemented — verified against the emulator, opt-in | [#53](https://github.com/MaximumTrainer/synthetic-fabricate/issues/53) |
| MongoDB | Atlas / self-hosted | ✅ Implemented — verified against `mongo:7` | [#54](https://github.com/MaximumTrainer/synthetic-fabricate/issues/54) |
| AWS DynamoDB | AWS | ✅ Implemented — verified against DynamoDB Local | [#55](https://github.com/MaximumTrainer/synthetic-fabricate/issues/55) |
| GCP Firestore | GCP | ✅ Implemented — verified against the emulator | [#56](https://github.com/MaximumTrainer/synthetic-fabricate/issues/56) |
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
  FirestoreSchemaDiscoverer    ← adapter
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

- ✅ `NoSqlSchemaDiscovererFactory` resolves registered providers by name (case-insensitive).
- ✅ `NoSqlSchemaDiscovererFactory` throws `NotSupportedException` for unknown providers.
- ✅ `CollectionMetadata`, `FieldDescriptor`, `PartitionKeyDescriptor` value-object construction.

### Integration tests (per-provider, against a running instance)

Every provider runs against a real instance, and none of them needs a cloud account: MongoDB against `mongo:7`,
DynamoDB against DynamoDB Local, Firestore against the Google Cloud CLI emulator, Cosmos DB against its own
emulator (opt-in, via `FABRICATE_COSMOS_EMULATOR=1`, because the image is far heavier than the rest).

`NoSqlProfilerTests` covers MongoDB and `NoSqlEmulatorTests` covers the other three. All four are seeded with the
same shaped documents — a field present on one document, explicitly null on a second, absent from a third; a
nested object; an array — so a difference between providers is a real difference and not a difference of fixture.
Each provider asserts the same properties:

- Collections and their fields are discovered, with the provider's own key model (a DynamoDB hash key, a Cosmos
  partition key, nothing for Firestore, which partitions internally).
- Aggregate statistics are reported per field: type, non-null and null counts, distinct count, min and max.
- A field absent from a document counts the same as one explicitly null.
- **No raw document content appears in the snapshot.** Values planted in the fixture are asserted absent from the
  serialised profile, and a string field's min/max is its length range rather than its content.

**Absence is reported, never inferred.** Each suite carries a test that fails if an emulator did not start, and
the coverage report names every provider as exercised, failed or not run. A suite that quietly does nothing is
worse than one that is missing (#91).

### Security tests

- Confirm that no connection strings, keys, or document values appear in log output.
- Confirm that sampling is bounded (e.g. a `maxSampleSize` parameter prevents runaway scans).

---

## Follow-up Implementation Issues

| Issue | Provider | Work |
|-------|----------|------|
| [#53](https://github.com/MaximumTrainer/synthetic-fabricate/issues/53) | Azure Cosmos DB | ✅ `CosmosDbSchemaDiscoverer` + profiler |
| [#54](https://github.com/MaximumTrainer/synthetic-fabricate/issues/54) | MongoDB | ✅ `MongoDbSchemaDiscoverer` + profiler |
| [#55](https://github.com/MaximumTrainer/synthetic-fabricate/issues/55) | AWS DynamoDB | ✅ `DynamoDbSchemaDiscoverer` + profiler |
| [#56](https://github.com/MaximumTrainer/synthetic-fabricate/issues/56) | GCP Firestore | ✅ `FirestoreSchemaDiscoverer` + profiler |
| [#71](https://github.com/MaximumTrainer/synthetic-fabricate/issues/71) | all four | ✅ `INoSqlDataProfiler` implementations |
| [#91](https://github.com/MaximumTrainer/synthetic-fabricate/issues/91) | all four | ✅ Verified against running instances rather than mocks |

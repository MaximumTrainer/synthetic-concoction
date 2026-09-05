# CLI Quick Start: Generate Synthetic Data for a SQLite Database

This guide walks you through using all five Fabricate CLI commands against a sample SQLite database.

## Prerequisites

- .NET 10 SDK installed
- Repository cloned and built (`dotnet build Fabricate.slnx`)

## Step 1: Create a Sample SQLite Database

Use the `sqlite3` CLI tool (or any SQLite client) to create a sample database:

```bash
sqlite3 sample.db <<'SQL'
CREATE TABLE departments (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL
);

CREATE TABLE employees (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  department_id INTEGER NOT NULL REFERENCES departments(id),
  email TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  manager_id INTEGER REFERENCES employees(id),
  status TEXT NOT NULL DEFAULT 'active'
);

CREATE TABLE orders (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  employee_id INTEGER NOT NULL REFERENCES employees(id),
  amount DECIMAL(10,2) NOT NULL,
  created_at DATETIME NOT NULL
);
SQL
```

## Step 2: Discover the Schema

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --database sample \
  --seed 42
```

You will see JSON output listing all three tables with their columns, PKs, FKs, and constraints:

```json
{
  "name": "sample",
  "tables": [
    {
      "schema": "main",
      "name": "departments",
      "qualifiedName": "main.departments",
      "columns": [...],
      "primaryKey": ["id"],
      "foreignKeys": [],
      "uniqueConstraints": [],
      "indexes": []
    },
    ...
  ]
}
```

## Step 3: Profile the Schema

The `discover-profile` command surfaces diagnostics about the schema — self-referencing FKs, cycles, and unmapped types.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- discover-profile \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --database sample
```

Expected output includes a diagnostic noting that `employees.manager_id` is a self-referencing FK:

```json
{
  "diagnostics": [
    {
      "level": "Info",
      "message": "Table main.employees has self-referencing FK: manager_id → id"
    }
  ]
}
```

Exit code is 1 if any diagnostics are found.

## Step 4: Create a Rules File

Create a file called `rules.yaml` in the project root:

```yaml
version: "1"
tables:
  - table: "main.employees"
    columns:
      - column: "email"
        strategy: "Email"
      - column: "name"
        strategy: "Name"
      - column: "status"
        fixedValue: "active"
  - table: "main.orders"
    columns:
      - column: "amount"
        distribution:
          "9.99": 0.4
          "49.99": 0.35
          "99.99": 0.25
```

## Step 5: Generate Data

Run the full generation pipeline with the rules file:

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --database sample \
  --seed 42 \
  --rows 20 \
  --rules ./rules.yaml \
  --compliance-profile Default \
  --output ./artifacts
```

On success you will see:

```
Generated 60 rows across 3 tables.
```

### Output Directory

```
artifacts/
  csv/
    main.departments.csv
    main.employees.csv
    main.orders.csv
  json/
    main.departments.json
    main.employees.json
    main.orders.json
  sql/
    main.departments.sql
    main.employees.sql
    main.orders.sql
  summary.json
```

### Inspect the CSV

```bash
cat artifacts/csv/main.employees.csv
```

```csv
id,department_id,email,name,manager_id,status
1,3,alice@example.com,Alice Johnson,,active
2,1,bob@example.com,Bob Smith,1,active
...
```

Note that `manager_id` for row 0 (Alice) is empty (null) — the self-referencing FK root.

### Inspect summary.json

```bash
cat artifacts/summary.json
```

```json
{
  "startedAt": "2024-06-01T10:00:00Z",
  "completedAt": "2024-06-01T10:00:01Z",
  "tableCount": 3,
  "rowCount": 60,
  "validationIssueCount": 0,
  "messages": []
}
```

## Step 6: Validate Without Exporting

The `validate` command runs generation and validation only — no files are written.

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- validate \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --seed 42 \
  --rows 20
```

Exit code 0 means clean. Exit code 3 means validation issues were found (details on stderr).

## Step 7: Export a Single Format

Use the `export` command to generate and write one format only:

```bash
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- export \
  --provider sqlite \
  --connection "Data Source=./sample.db" \
  --seed 42 \
  --rows 20 \
  --format sql \
  --output ./sql-only
```

Inspect the SQL:

```bash
cat sql-only/main.employees.sql
```

```sql
INSERT INTO "main"."employees" ("id","department_id","email","name","manager_id","status")
VALUES (1,3,'alice@example.com','Alice Johnson',NULL,'active');
INSERT INTO "main"."employees" ("id","department_id","email","name","manager_id","status")
VALUES (2,1,'bob@example.com','Bob Smith',1,'active');
```

## Summary of Exit Codes

| Command | Code | Meaning |
|---|---|---|
| Any | 0 | Success |
| Any | 1 | Invalid rules file or unknown format |
| `generate` | 2 | Validation issues found (data written) |
| `validate` | 3 | Validation issues found (no files written) |

## Next Steps

- Customise your rules file: see [Rules DSL Reference](rules-dsl.md)
- Apply HIPAA masking: see [Compliance Profiles](compliance-profiles.md)
- Call the REST API: see [REST API How-To](rest-api.md)

## NoSQL providers

`discover` and `discover-profile` accept the four document stores as well as `sqlite` and `postgres`:

```bash
fabricate discover --provider mongodb   --connection "mongodb://host:27017"        --database clinic
fabricate discover --provider cosmosdb  --connection "$COSMOSDB_CONNECTION_STRING" --database clinic
fabricate discover --provider dynamodb  --connection ""                            --database us-east-1
fabricate discover --provider firestore --connection "my-gcp-project"              --database "(default)"

fabricate discover-profile --provider mongodb --connection "mongodb://host:27017" --database clinic
```

`--connection` is the provider's own connection string, or empty to use ambient credentials — an IAM role for
DynamoDB, Application Default Credentials for Firestore. Each provider also reads its usual environment variable
(`MONGODB_CONNECTION_STRING`, `COSMOSDB_CONNECTION_STRING`, `GOOGLE_CLOUD_PROJECT`) when `--connection` is empty.

Both commands print JSON in the same shape as the relational ones. Note that **collection metadata has no
foreign-key graph** — document stores do not declare relationships, so there is none to discover. See the
[user guide](../user-guide.md#nosql-discovery-and-profiling) for the metadata model's limits.

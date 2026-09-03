# Rules DSL Reference

The Fabricate Rules DSL lets you override generation strategies for specific tables and columns. Rules files can be written in **YAML** or **JSON** and passed to the CLI with `--rules <path>`.

## File Format

Both formats are interchangeable. Use whichever your team prefers.

### YAML

```yaml
version: "1"
tables:
  - table: "public.users"
    columns:
      - column: "email"
        strategy: "Email"
```

### JSON

```json
{
  "version": "1",
  "tables": [
    {
      "table": "public.users",
      "columns": [
        { "column": "email", "strategy": "Email" }
      ]
    }
  ]
}
```

## Top-Level Fields

| Field | Type | Required | Description |
|---|---|---|---|
| `version` | `string` | Yes | Must be `"1"`. |
| `tables` | `array` | No | Zero or more table rule entries. |

## Table Rule Fields

| Field | Type | Required | Description |
|---|---|---|---|
| `table` | `string` | Yes | Qualified table name, e.g. `"public.users"` (PostgreSQL) or `"main.users"` (SQLite). |
| `columns` | `array` | No | Zero or more column rule entries for this table. |

## Column Rule Fields

| Field | Type | Description |
|---|---|---|
| `column` | `string` | Column name. Required. |
| `strategy` | `string` | `DataKind` name (e.g. `"Email"`, `"Integer"`, `"Name"`). Overrides inferred type. |
| `fixedValue` | `any` | Emit this exact value for every row. Overrides `strategy`. |
| `nullRate` | `number [0,1]` | Probability that the column is `null`. `0.0` = never null, `1.0` = always null. |
| `seedOffset` | `integer` | Added to the global seed for this column''s generator. Use to decorrelate columns that would otherwise produce correlated output. |
| `distribution` | `map<string, number>` | Weighted discrete distribution. Keys are string values; weights must sum to ≤ 1.0. |
| `jsonPaths` | `array` | Per-path strategies for `JSON`/`JSONB` columns. See [JSON Path Strategies](#json-path-strategies). |

## Strategy Values

`strategy` accepts any `DataKind` name (case-insensitive):

| Strategy name | Generated value |
|---|---|
| `Boolean` | `true` or `false` |
| `Integer` | Random integer |
| `Long` | Random 64-bit integer |
| `Decimal` | Random decimal |
| `Double` | Random double |
| `String` | Random alphanumeric string |
| `Guid` | Random GUID |
| `Date` | Random date |
| `DateTime` | Random datetime |
| `Json` | Empty JSON object `{}` |
| `Binary` | Random bytes (base64 encoded) |
| `Email` | Valid email address |
| `Phone` | E.164 phone number |
| `Name` | Full person name |
| `FirstName` | Given name |
| `LastName` | Family name |
| `Address` | Street address |
| `PostalCode` | Postal/ZIP code |
| `CountryCode` | ISO 3166-1 alpha-2 |
| `Url` | HTTP/HTTPS URL |
| `IpAddress` | IPv4 address |
| `Currency` | ISO 4217 currency code |
| `CompanyName` | Company name |
| `Text` | Long-form lorem ipsum text |
| `Uuid` | UUID string |
| `TimestampTz` | ISO 8601 timestamp with timezone |

## fixedValue

Emits a literal value for every generated row. The value is serialised as-is into the output.

```yaml
- column: "status"
  fixedValue: "active"

- column: "is_verified"
  fixedValue: true

- column: "score"
  fixedValue: 100
```

`fixedValue` takes the highest precedence — it overrides `strategy`, `distribution`, and compliance profile masking.

## nullRate

```yaml
- column: "middle_name"
  nullRate: 0.7
```

70% of rows will have `null` for `middle_name`. The remaining 30% use the inferred or specified strategy.

Combining with `distribution`:

```yaml
- column: "tier"
  nullRate: 0.1
  distribution:
    silver: 0.5
    gold: 0.3
    platinum: 0.2
```

10% of rows → `null`. 90% are distributed: ~45% silver, ~27% gold, ~18% platinum.

## seedOffset

By default all columns in the same table share the same generator seed derived from the global `--seed`. If two columns produce correlated values, add different `seedOffset` values:

```yaml
- column: "first_name"
  strategy: "FirstName"
  seedOffset: 1

- column: "last_name"
  strategy: "LastName"
  seedOffset: 2
```

## distribution

A weighted map from string value to weight. Weights are normalised at generation time.

```yaml
- column: "country"
  distribution:
    US: 0.6
    GB: 0.2
    DE: 0.1
    FR: 0.1
```

All weights sum to 1.0. If they sum to less than 1.0, a random value is also generated for the remainder.

## JSON Path Strategies

For `JSON` or `JSONB` columns, use `jsonPaths` to specify per-path strategies:

### YAML

```yaml
- column: "metadata"
  jsonPaths:
    - path: "$.email"
      strategy: "Email"
    - path: "$.address.city"
      strategy: "String"
    - path: "$.score"
      strategy: "Integer"
    - path: "$.tag"
      fixedValue: "synthetic"
    - path: "$.optional_field"
      strategy: "String"
      nullRate: 0.5
```

### JSON

```json
{
  "column": "metadata",
  "jsonPaths": [
    { "path": "$.email", "strategy": "Email" },
    { "path": "$.address.city", "strategy": "String" },
    { "path": "$.score", "strategy": "Integer" },
    { "path": "$.tag", "fixedValue": "synthetic" },
    { "path": "$.optional_field", "strategy": "String", "nullRate": 0.5 }
  ]
}
```

### Path Notation

- Dollar-dot syntax: `$.fieldName`, `$.parent.child`, `$.a.b.c`
- Array indexing (`$.items[0]`) is **not** supported.

### JsonPath Rule Fields

| Field | Type | Description |
|---|---|---|
| `path` | `string` | Dollar-dot path. Required. |
| `strategy` | `string` | `DataKind` name. Defaults to `"String"`. |
| `fixedValue` | `any` | Fixed value for this path. Overrides `strategy`. |
| `nullRate` | `number [0,1]` | Probability this path is omitted from the JSON object. |

## Precedence Merge

When multiple rule sources are combined (e.g. a global defaults file and a project-specific file), Fabricate merges them in priority order:

```
global defaults  <  project defaults  <  table rules  <  column rules
```

More specific rules win. Merging is performed by `IRuleConfigurationService.Merge()`. The CLI applies the rules you pass directly as the highest-precedence layer.

## Validation

When you pass `--rules`, Fabricate validates the file before generation:

- `version` must be `"1"`
- `table` must be a non-empty string
- `column` must be a non-empty string
- `nullRate` must be between 0 and 1
- `strategy` must be a recognised `DataKind` name (or the column receives a warning and falls back to inferred type)

Validation errors are printed to stderr and the CLI exits with code 1.

## Complete YAML Example

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
      - column: "discount"
        nullRate: 0.3
        distribution:
          silver: 0.5
          gold: 0.3
          platinum: 0.2
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

  - table: "public.orders"
    columns:
      - column: "status"
        distribution:
          pending: 0.4
          processing: 0.3
          shipped: 0.2
          cancelled: 0.1
      - column: "notes"
        nullRate: 0.6
        strategy: "Text"
```

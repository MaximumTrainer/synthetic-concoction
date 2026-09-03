# Compliance Profiles

Compliance profiles automatically mask sensitive fields to reduce the risk of generating realistic PII in environments that must comply with data privacy regulations.

## Available Profiles

| Profile | `--compliance-profile` value | Description |
|---|---|---|
| Default | `Default` | No masking. All fields generate realistic values. |
| Healthcare | `Healthcare` | HIPAA-inspired: masks contact and demographic fields. |
| Finance | `Finance` | PCI-inspired: masks Healthcare fields plus financial identifiers. |

## Default Profile

The `Default` profile applies no masking. All `DataKind` values produce realistic synthetic output.

This is the default when `--compliance-profile` is not specified.

## Healthcare Profile (HIPAA-style)

The `Healthcare` profile masks the following field categories:

| DataKind / Field Category | Treatment |
|---|---|
| `Email` | Replaced with anonymised placeholder (e.g. `masked-1@redacted.invalid`) |
| `Phone` | Replaced with anonymised placeholder (e.g. `+10000000001`) |
| `Name` | Replaced with anonymised placeholder (e.g. `Redacted Name`) |
| `FirstName` | Replaced with anonymised placeholder |
| `LastName` | Replaced with anonymised placeholder |
| `Address` | Replaced with anonymised placeholder |
| `PostalCode` | Replaced with anonymised placeholder |

These correspond to Protected Health Information (PHI) field categories under HIPAA.

## Finance Profile (PCI-style)

The `Finance` profile inherits all `Healthcare` masking and additionally masks financial fields:

| DataKind / Field Category | Treatment |
|---|---|
| All Healthcare fields | Masked as above |
| `Currency` | Anonymised or zeroed |
| Financial identifier columns | Masked |

This profile is appropriate for environments where PCI DSS compliance applies to cardholder data or financial records.

## CLI Usage

```bash
# Healthcare masking
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider postgres \
  --connection "Host=localhost;Database=prod_clone;Username=dev;Password=dev" \
  --compliance-profile Healthcare \
  --rows 1000 \
  --output ./artifacts

# Finance masking
dotnet run --project ./Fabricate.Cli/Fabricate.Cli.csproj -- generate \
  --provider sqlite \
  --connection "Data Source=./finance.db" \
  --compliance-profile Finance \
  --rows 500
```

## API Usage

Pass the `complianceProfile` field in the run request body:

```http
POST /runs
{
  "projectId": "3fa85f64-...",
  "seed": 42,
  "rowCounts": { "public.users": 100 },
  "complianceProfile": "Healthcare"
}
```

## Interaction with Rules

Rules (`--rules`) and compliance profiles interact according to the following precedence rules:

| Scenario | Result |
|---|---|
| Column has `fixedValue` in rules | `fixedValue` always wins — compliance masking does NOT override it. |
| Column has `strategy` in rules AND compliance would mask it | Compliance profile masking takes effect (compliance wins for masked categories). |
| Column has no rules entry AND compliance masks it | Column is masked. |
| Column has no rules entry AND compliance does not mask it | Column uses inferred strategy or rules strategy. |

In summary: **`fixedValue` wins over everything; compliance wins over `strategy` for sensitive categories.**

## ComplianceDecisions Output

Every column affected by a compliance profile is recorded in `GenerationResult.ComplianceDecisions`. Each entry includes:

| Field | Description |
|---|---|
| `Table` | Qualified table name |
| `Column` | Column name |
| `Strategy` | The `SensitiveFieldStrategy` applied |
| `Classification` | The detected sensitive category (e.g. `"Email"`, `"Name"`) |
| `Reason` | Human-readable explanation |
| `Source` | `ComplianceProfile` or `UserRule` |

Example entry in `summary.json`:

```json
{
  "table": "public.users",
  "column": "email",
  "strategy": "Mask",
  "classification": "Email",
  "reason": "Healthcare profile: email addresses are PHI and must be masked.",
  "source": "ComplianceProfile"
}
```

## Extending Compliance

To add a custom compliance profile, implement the `ISensitiveFieldClassifier` port in `Fabricate.Application` and register it in the DI container. Custom profiles can define any masking logic based on column name, `DataKind`, or schema metadata.

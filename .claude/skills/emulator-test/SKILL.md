---
name: emulator-test
description: Add a container-backed integration test for a database, cloud store, or other external service using Testcontainers. Covers image choice, readiness, the start guard, and the coverage report. Use when asked to test an adapter against a real instance, add an emulator test, or verify something that currently only has mocks.
---

# Add a container-backed integration test

The goal is an adapter exercised against the real thing, in CI, with no cloud account. An adapter that has only
ever run against a mock has not been tested — its query syntax, its type mapping and its connection-string
handling are all still guesses.

## 1. Prefer an emulator to an account

Nearly everything has one. Currently in use:

| Service | Image |
| --- | --- |
| MongoDB | `mongo:7` |
| PostgreSQL | `postgres:16` |
| DynamoDB | `amazon/dynamodb-local` |
| Firestore | `gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators` |
| Cosmos DB | `azure-cosmos-emulator:vnext-preview` |
| S3 | MinIO |
| Azure Blob | Azurite |
| GCS | fake-gcs-server |

Pin the tag. Use the image-parameter constructor — `new ContainerBuilder("image:tag")`, `new MongoDbBuilder("mongo:7")` —
the parameterless ones are obsolete and fail the build.

**Cosmos DB specifically:** use `vnext-preview`, not `:latest`. The older image is 3 GB, writes its log to a file
inside the container (so no log-based wait strategy can ever match), and then spins at full CPU without ever
serving under WSL2. `vnext-preview` is 1.7 GB and serves in about ten seconds.

## 2. Readiness is a real client call, not a log line

A log-line wait strategy breaks silently when an image changes its output or logs to a file. The container also
reports *running* long before it accepts a request.

Build with no wait strategy and let the first real call through the production client be the readiness check,
retrying with a deadline:

```csharp
var deadline = DateTime.UtcNow + timeout;
while (true)
{
    try { await SeedAsync(client); return; }
    catch when (DateTime.UtcNow < deadline) { await Task.Delay(TimeSpan.FromSeconds(2)); }
}
```

That way readiness means the thing the test needs, not the thing the image happens to print.

Where the service *does* log a stable line, `Wait.ForUnixContainer().UntilMessageIsLogged(...)` is fine — the
Firestore emulator's `"Dev App Server is now running"` is reliable.

## 3. Seed through the production parser

Where the adapter accepts a connection string, seed the fixture through **the adapter's own** parser rather than
building a client by hand. That tests the format inbound as well as outbound.

This is how #91 caught a real defect: `DynamoDbSchemaDiscoverer` parsed `region=...;serviceUrl=...` while
`DynamoDbDataProfiler` treated the whole string as a service URL — and the CLI passes the identical `--connection`
to both commands. Neither had ever connected, so nothing caught it. The shared parsers now live in
`Fabricate.Infrastructure/Schema/*ConnectionString.cs`, with `InternalsVisibleTo("Fabricate.Tests")`.

## 4. Same fixture across providers

Seed every provider with the same shaped documents, so a difference between providers is a real difference and
not a difference of fixture. The shape that has earned its place:

- a field present on one document, explicitly null on a second, **absent** from a third
- a nested object
- an array
- a recognisable secret value planted in the data, asserted absent from any output

## 5. The start guard — mandatory

Every self-skipping suite needs one test that fails when the fixture should have started and did not:

```csharp
[Fact]
public void BothEmulatorsStartedWhenDockerIsAvailable()
{
    output.WriteLine(fixture.Report());
    if (!fixture.DockerAvailable) return;

    fixture.DynamoConnectionString.Should().NotBeNull(
        "DynamoDB Local must start when Docker is available; it failed with: {0}", fixture.DynamoFailure);
}
```

And a report naming each provider **exercised / failed / not run**, distinguishing "not asked for" from "asked
for and broken". Without this the suite is green and empty — see `/test-audit`.

## 6. Mechanics

- Honour `FABRICATE_SKIP_DOCKER_TESTS=1`.
- Store the startup exception on the fixture rather than letting it escape, so one broken emulator does not take
  the others down — but surface it through the guard.
- `[CollectionDefinition("X", DisableParallelization = true)]` whenever the fixture mutates process-wide state
  (environment variables, ambient credentials).
- Restore every environment variable in `DisposeAsync`.
- Heavy images (Cosmos) go behind an opt-in variable and run in `integration-tests.yml`, not on every push.

## 7. Wire it up

Add the package to `Fabricate.Tests.csproj`, then check whether the suite belongs in `ci.yml` (no credentials
needed, fast enough) or `integration-tests.yml` (heavy or gated). Update
`docs/how-to/ci-integration-secrets.md` **and its HTML twin** with what the suite needs.

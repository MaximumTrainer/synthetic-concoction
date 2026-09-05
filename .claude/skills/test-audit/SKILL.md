---
name: test-audit
description: Find tests that pass while doing nothing - env-gated early returns, assertions over empty collections, swallowed fixture failures, container suites with no start guard. Use when asked to audit test coverage, check whether tests actually run, or verify a suite is not silently skipping.
---

# Find tests that pass while testing nothing

A failing test is cheap. A test that passes without exercising anything is expensive, because it buys confidence
that was never earned. This codebase has shipped that bug three separate times:

- **#90** — five Azure Blob and GCS tests passed while their emulator had not started. Only a "did both
  emulators start" guard caught it.
- **#91** — three of four NoSQL profilers had never run against a database. Their tests were gated behind cloud
  credentials nobody had configured, so the suite was green and empty.
- `NoSqlAdapterIntegrationTests` — every test looped over a collection and asserted per element, so it passed
  even when the collection was empty, which it always was.

## The patterns to hunt

**1. Gated early return with no report.** The whole suite disappears when a variable is unset, and nothing says
so.

```csharp
if (string.IsNullOrWhiteSpace(connectionString)) return;   // silent
```

Grep: `rg -n "is null\) return;|IsNullOrWhiteSpace\(.*\)\) return;" --glob "**/*Tests.cs"`

**2. Assertions inside a loop with no non-empty check.** `foreach (var x in items) x.Should()...` passes
vacuously on an empty `items`.

Grep: `rg -n -B2 -A6 "foreach.*in .*\)\s*$" --glob "**/*Tests.cs"` and look for a missing
`Should().NotBeEmpty()` or `ContainSingle()` before the loop.

**3. Swallowed fixture failure.** `catch { }` around container startup turns "the emulator is broken" into
"every test skipped", indistinguishable from "no Docker".

Grep: `rg -n -A3 "catch \(Exception" --glob "**/Integration/*.cs"`

**4. A container fixture with no start guard.** If every test in a class self-skips on a null connection string,
one test must fail when the container *should* have started and did not.

**5. A wait strategy that cannot match.** A log-line wait against an image that logs to a file waits forever, and
the failure looks like a skip. Readiness should be a real client call.

## What a correct self-skipping suite looks like

`Fabricate.Tests/Integration/NoSqlEmulatorTests.cs` is the reference. Two things make it honest:

```csharp
[Fact]
public void BothEmulatorsStartedWhenDockerIsAvailable()
{
    output.WriteLine(fixture.Report());          // says what ran and what did not
    if (!fixture.DockerAvailable) return;        // no Docker at all is a real skip

    fixture.DynamoConnectionString.Should().NotBeNull(
        "DynamoDB Local must start when Docker is available; it failed with: {0}", fixture.DynamoFailure);
}
```

- The guard **fails** when the fixture should have started and did not, carrying the underlying exception.
- The report distinguishes *exercised* / *failed* / *not run*, and distinguishes "not asked for" from "asked for
  and broken" — those are different facts and printing the first when the second is true is the bug.

## Reporting

For each finding give the file and line, which pattern it is, and what it would take to make the test real. Rank
by how much false confidence it buys: a security or data-boundary assertion that never runs is worse than a
formatting one.

Where a test cannot be made real without credentials, say so and propose `/gap-issue` rather than deleting it —
though a test that iterates an always-empty list should simply be deleted, because it is worse than nothing.

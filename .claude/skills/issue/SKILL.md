---
name: issue
description: Implement a GitHub issue end to end - read its acceptance criteria, write the failing test first, implement, build, run the affected suites, verify each criterion explicitly, commit and close. Use when asked to "do issue N", "complete issue N", "progress with issue N", or "work through the open issues".
---

# Work an issue to completion

The unit of work in this repo is a GitHub issue with written acceptance criteria. Closing one means every
criterion is **demonstrably** met, not that the code was written.

## 1. Read the issue, not a summary of it

```bash
gh issue view <n>
```

Copy the acceptance criteria somewhere you can tick them off. They are the definition of done and they are
usually more specific than the issue title suggests. Read any issue it references — a criterion often only makes
sense against the earlier work it is correcting.

If the issue names a file or a symbol, open it before planning. Issues in this repo are written against real
code and the details are load-bearing.

## 2. Plan outside-in

Start at the boundary the user touches — an endpoint, a CLI command, a port interface — and work inward. That
order surfaces a wrong abstraction while it is still cheap.

A new capability is a port in `Fabricate.Application/Abstractions/Ports.cs` plus an adapter in
`Fabricate.Infrastructure`. Check whether a port already exists before adding one.

## 3. Write the failing test first

One test per acceptance criterion where the criterion is testable. The test name should state the behaviour, not
the method under test: `AFieldMissingFromSomeDocumentsIsCountedAsAbsent`, not `TestProfileAsync`.

Watch for the criterion that is about **absence** — "no raw content in the snapshot", "the secret is not
returned". Those need a test that plants a recognisable value and asserts it is gone. They are the criteria most
often waved through.

If a test cannot fail for the reason you intend, it is not yet a test. Run it and see it fail first.

## 4. Implement, then build, then test

```powershell
$env:MSBuildSDKsPath=$null; $env:MSBUILD_EXE_PATH=$null; dotnet build 2>&1 | Select-String "error|Build succeeded"
```

Gate on `Build succeeded` before running anything. `--no-build` against stale output has produced both false
passes and false failures here.

Iterate with `--filter`, then run the full `dotnet test Fabricate.Tests` once before committing (~13 min).

## 5. Walk the criteria out loud

Before committing, go through the list and state for each one: met, and what demonstrates it. This is where
half-finished work gets caught.

**When a criterion cannot be met** — no cloud account, no API key, no credentials — do not quietly close the
issue and do not silently drop the criterion:

1. Finish every other criterion in full.
2. Say plainly which one is unmet and why.
3. Either leave the issue open with a comment recording the gap, or file a follow-up with `/gap-issue`.

This rule is not bureaucracy. Issues #90 and #91 both exist because earlier work closed with parts unverified,
and the gap only surfaced later.

## 6. Commit

`type(scope): imperative summary (#n)` with a prose body explaining **why**, naming any defect the work
uncovered — those are the most valuable lines in the log.

```
Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
```

## 7. Close

```bash
gh issue close <n> --comment "..."
```

The comment lists each acceptance criterion and what satisfies it, plus anything left unverified. Someone
reading it in six months should be able to tell what was actually checked.

## Working through all open issues

`gh issue list` first, then order by dependency rather than issue number — an issue that changes a port should
land before one that consumes it. Do them one at a time, each with its own commit. Report at the end which
issues closed and which are blocked, with the reason for each block.

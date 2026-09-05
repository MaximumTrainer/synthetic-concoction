---
name: gap-issue
description: File a GitHub issue recording what a piece of work deliberately left unverified, so the gap is tracked rather than forgotten. Use when closing work with a criterion unmet, when something is wired but untested, or when asked to "create an issue for the remaining gaps".
---

# File an issue for what was left unverified

Work often lands with a part that could not be verified — no cloud account, no API key, no way to reach the real
service. That is acceptable. Closing the issue as though it were fully verified is not.

This repo's practice is to record the gap as its own issue, phrased so that nobody later mistakes "the code
exists" for "the code works". Issue #91 came from exactly this and immediately found two real defects in
adapters that had never connected to anything.

## What makes a good gap issue

**Name the shape of the gap, not just the missing test.** The recurring one here is *code written to a tested
interface, where nothing has exercised it against the real thing*. Say that, because it tells the reader what
class of bug is hiding.

**Separate what IS covered from what IS NOT.** Be specific. From #91:

> What *is* covered for all four is `NoSqlProfileAccumulator` — the shared aggregation, the presence-ratio
> arithmetic, and the redaction rules. What is **not** covered per provider is each adapter's own query and type
> mapping: the Cosmos `SELECT VALUE COUNT(1)` aggregate, DynamoDB's `AttributeValue` union, Firestore's
> `Count()` aggregate and its `Timestamp` handling. Those are exactly where an adapter bug hides.

That paragraph is the value of the issue. Without it the reader assumes either everything or nothing is tested.

**Say what would close it.** An emulator image, a credential, a workflow. Prefer an emulator to an account,
since that runs in CI forever without anyone holding a secret.

**Distinguish it from adjacent issues.** If a similar issue exists, say how this one differs — otherwise it gets
closed as a duplicate by someone who read only the title.

## Acceptance criteria a green run cannot fake

This is the part that matters most. Write criteria that a silently-skipping suite would **fail**:

- ✅ "DynamoDB and Firestore run against emulators in CI with no credentials; Cosmos DB runs opt-in, and its
  absence is reported rather than silently skipped."
- ✅ "Both suites report what was exercised versus skipped, so a green run cannot be mistaken for full coverage."
- ❌ "Add integration tests for the NoSQL profilers." — satisfied by a suite that does nothing.

Include the no-raw-content or no-secret-leak criterion explicitly where the work touches customer data. Those
are the assertions most often waved through.

## Structure

```markdown
## Problem
<the shape of the gap; one subsection per area, each saying what is and is not covered>

## Requirements
<what to build, emulator-first, with the specific queries or behaviours that need exercising>

## Acceptance Criteria
- [ ] <phrased so a silent skip fails>

## Notes
<which issues supply credentials, workflows or conventions this should reuse rather than duplicate>
```

## Filing it

```bash
gh issue create --title "..." --body-file <path> --label "..."
```

Write the body to a file in the scratchpad rather than passing it inline — heredocs mangle backticks and
markdown tables.

Then reference the new issue from wherever the gap lives: a comment on the issue being closed, and a note in the
relevant doc if a reader would otherwise assume the capability is verified.

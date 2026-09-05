---
name: docs-sync
description: Update a doc in docs/ together with its hand-maintained HTML twin, without mangling the page. Use when changing anything under docs/, or when a code change needs a documentation update.
---

# Update a doc and its HTML twin

Every `docs/**/*.md` has a `.html` twin that is **hand-maintained and checked in**. Both are published; changing
only the markdown ships a stale page.

```
docs/user-guide.md                    docs/user-guide.html
docs/nosql-provider-roadmap.md        (no twin — markdown only)
docs/how-to/self-hosting.md           docs/how-to/self-hosting.html
docs/how-to/ci-integration-secrets.md docs/how-to/ci-integration-secrets.html
```

Check for a twin before assuming there is one.

## Edit the section, do not regenerate the page

This is the rule that matters. A naive markdown→HTML converter looks like it works and quietly does three
things wrong:

- **splits a table** wherever the markdown has a blank line inside it, emitting a second `<table>` whose first
  data row becomes a `<thead>`
- **flattens an ordered list** into one run-on `<p>` with literal backticks
- **rewrites `.html` links to `.md`**, because it copies the href from the markdown

All three happened on `self-hosting.html` and had to be reverted. Hand-edit the changed section instead. It is
faster than repairing a regenerated page.

## The page skeleton

```html
<!DOCTYPE html>
<html lang="en">
<head>… <title>Title — Fabricate</title> <style></style></head>
<body>
<nav>…</nav>
<div class="page">
  … content …
</div>
<footer>…</footer>
</body>
</html>
```

Insert new content inside `<div class="page">`, anchored on the neighbouring `<h2>`/`<h3>`.

## Conventions

| Thing | Markdown | HTML twin |
| --- | --- | --- |
| Links between docs | `ci-integration-secrets.md` | `ci-integration-secrets.html` |
| Links to repo files | `../../.github/workflows/ci.yml` | same |
| Quotes in a code block | verbatim | `&quot;` |
| Apostrophe in prose | verbatim | verbatim, **not** `&#x27;` |
| Heading anchor | — | `<h2 id="kebab-case-of-title">` |
| Em dash | `—` | `—` |

Tables: `<table><thead><tr><th>…</th></tr></thead><tbody><tr><td>…</td></tr></tbody></table>`, one row per line.
Lists: `<ul><li>…</li><li>…</li></ul>` collapsed onto a single line.

## Check before committing

```bash
python - <<'EOF'
import io, re
s = io.open('docs/how-to/self-hosting.html', encoding='utf-8').read()
for tag in ('table','thead','tbody','ul','ol','pre','p','h1','h2','h3','div'):
    o, c = len(re.findall(rf'<{tag}[ >]', s)), len(re.findall(rf'</{tag}>', s))
    if o != c: print(f'MISMATCH {tag}: {o} open, {c} close')
EOF
```

Then `git diff --stat docs/` — if the HTML diff is much larger than the markdown diff, something regenerated the
page. Revert and edit by hand.

## Which doc to update

| Change | Doc |
| --- | --- |
| A new environment variable | `docs/how-to/self-hosting.md` **and** `.env.example` |
| A new test gate or secret | `docs/how-to/ci-integration-secrets.md` |
| A new CLI command or flag | `docs/how-to/cli-quickstart.md`, `docs/user-guide.md` |
| A new endpoint | `docs/how-to/rest-api.md` |
| NoSQL provider status | `docs/nosql-provider-roadmap.md` |

State limits honestly. If something is wired but unverified, the doc says so — see the clarifying-question
matrix in `self-hosting.md`, which carries a *not yet run* row rather than an implied claim.

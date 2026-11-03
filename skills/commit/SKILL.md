---
name: commit
description: "Use when the user calls commit, /commit, or $commit to draft a commit message from the current changes and commit only after the user confirms."
---

# /commit

## Purpose
Draft a clean commit message from the current changes and commit only after confirmation.

## Rules
- Read `git status` and `git diff` first.
- Do not change code.
- Do not stage, commit, amend, push, or reset without explicit approval.
- Describe only changes present in the diff.
- Prefer one concise imperative subject line.

## Workflow
1. Inspect status and diff.
2. Identify the main purpose of the change.
3. Draft a commit subject, max about 72 characters when practical.
4. Add a short body only when it adds useful context.
5. Show the draft.
6. Ask for confirmation before committing.
7. After approval, commit exactly the reviewed changes.

## Output
### Commit Draft
`<subject>`

<body if useful>

### Changed Files
- `<path>`

`Commit this?`

## Stop Conditions
Never commit before explicit confirmation.

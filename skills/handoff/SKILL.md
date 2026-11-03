---
name: handoff
description: "Use when the user calls handoff, /handoff, or $handoff to write HANDOFF.md so a new chat or agent can continue the work."
---

# /handoff

## Purpose
Create `HANDOFF.md` so a new chat or agent can continue the work quickly.

## Rules
- Use facts from the current project and work completed.
- Be concise and specific.
- Include exact file paths and commands when useful.
- Do not include hidden reasoning or unnecessary conversation history.
- Overwrite `HANDOFF.md` only when the user asked for a handoff.

## Workflow
1. Inspect current git status/diff when available.
2. Summarize the task goal.
3. Record completed work.
4. Record remaining work in priority order.
5. Record important decisions and constraints.
6. List files changed or important to continue.
7. List checks run and their results.
8. Write `HANDOFF.md`.

## HANDOFF.md Format
# Handoff

## Goal
<current objective>

## Completed
- ...

## Remaining
1. ...

## Decisions / Constraints
- ...

## Important Files
- `<path>` - <role>

## Verification
- `<command>` - PASS/FAIL/not run

## Next Action
<single best next step>

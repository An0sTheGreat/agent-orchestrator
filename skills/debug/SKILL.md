---
name: debug
description: "Use when the user calls debug, /debug, or $debug to find the cause of a bug and make the smallest safe fix, with a failing test first."
---

# /debug

## Purpose
Find the cause of a bug and make the smallest safe fix.

## Rules
- Reproduce before fixing when possible.
- Prefer evidence over guesses.
- Add or identify a failing test/check before changing code.
- Change the fewest lines and files needed.
- Do not refactor unrelated code.
- Use the project's existing check command, including `run_checks` when available.

## Workflow
1. Read the bug report and relevant code.
2. Reproduce the failure or identify a reliable failing check.
3. State the likely root cause in one sentence.
4. Add a failing regression test when practical.
5. Make the smallest fix.
6. Run the focused test.
7. Run broader project checks.
8. Report what changed and any remaining risk.

## Output
### Cause
<root cause>

### Fix
- `<file>` - <small change>

### Verification
- <test/check>: PASS/FAIL

### Notes
- <remaining limitation, or `None`>

## Stop Conditions
- If the bug cannot be reproduced, do not invent a fix. Report the evidence gathered and the next best diagnostic step.
- If the requested fix would require broad redesign, stop and recommend `/plan` first.

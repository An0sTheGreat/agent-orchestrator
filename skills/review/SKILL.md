---
name: review
description: "Use when the user calls review, /review, or $review to review changed code for concrete bugs and regressions (read-only)."
---

# /review

## Purpose
Review changed code for concrete bugs and regressions.

## Rules
- Read-only. Do not edit files.
- Prefer `git diff`, changed files, and nearby code.
- Report only actionable findings.
- Do not praise, summarize style, or invent theoretical issues.
- One finding per line.
- Include file and line when available.
- Order findings by severity.

## Workflow
1. Read the diff or changed files.
2. Check correctness, edge cases, state handling, errors, security, compatibility, and tests.
3. Verify each finding against the surrounding code.
4. Remove weak or duplicate findings.
5. Return findings only.

## Output
### Findings
- `[HIGH|MEDIUM|LOW] path:line - Problem: <issue>. Fix: <specific fix>.`

If no real issues are found, return:
`No actionable findings.`

## Stop Conditions
Do not modify code. Do not turn uncertain guesses into findings.

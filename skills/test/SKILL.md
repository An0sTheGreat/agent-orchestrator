---
name: test
description: "Use when the user calls test, /test, or $test to add focused tests for existing code or a recent change and run them."
---

# /test

## Purpose
Add focused tests for existing code or a recent change and run them.

## Rules
- Read the target code and existing test style first.
- Prefer focused tests over broad rewrites.
- Test behavior, not implementation details, unless necessary.
- Cover the main path plus important edge/failure cases.
- Do not change production code unless the user asked for fixes too.
- Use existing project test commands and `run_checks` when available.

## Workflow
1. Identify the target behavior.
2. Find nearby tests and copy their conventions.
3. List the cases to cover.
4. Add the smallest useful tests.
5. Run the focused test file or suite.
6. Run broader checks when practical.
7. Report coverage added and results.

## Output
### Tests Added
- `<test file>` - <behavior covered>

### Cases
- <case>

### Results
- `<command>` - PASS/FAIL

### Notes
- <gap or `None`>

## Stop Conditions
If expected behavior is unclear, test only behavior that is already established by code, docs, or the user request.

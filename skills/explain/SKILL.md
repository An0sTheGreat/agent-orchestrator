---
name: explain
description: "Use when the user calls explain, /explain, or $explain to give a read-only tour of a file, feature or project."
---

# /explain

## Purpose
Give a clear, read-only tour of a file, feature, or project.

## Rules
- Do not edit files.
- Read only enough code to explain the requested scope.
- Explain concrete behavior before abstractions.
- Use exact file/function names.
- Keep jargon low; define necessary terms briefly.

## Workflow
1. Identify the requested scope.
2. Find the entry point or main file.
3. Trace the important calls/data flow.
4. Explain the major pieces and how they connect.
5. Point to the best place to start reading.

## Output
### What It Does
<short explanation>

### How It Flows
1. `<file/function>` - <role>
2. ...

### Key Pieces
- `<path>` - <purpose>

### Start Here
`<best file/function>` - <why>

## Stop Conditions
If the project is large, explain only the requested area instead of attempting a full codebase tour.

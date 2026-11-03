---
name: plan
description: "Use when the user calls plan, /plan, or $plan to write a short implementation plan with a check per step, then wait for approval."
---

# /plan

## Purpose
Create a short implementation plan before editing code.

## Rules
- Read the relevant files first.
- Do not edit files.
- Do not run destructive commands.
- Keep the plan small and ordered.
- Each step must include one clear check.
- Stop after the plan and wait for approval.

## Workflow
1. Restate the requested outcome in one sentence.
2. Inspect only the files needed to understand the task.
3. Identify the smallest set of files likely to change.
4. Write 3-8 numbered steps.
5. Add a verification check to every step.
6. List important risks or unknowns only if they matter.
7. End with: `Ready for approval.`

## Output
### Goal
<one sentence>

### Plan
1. <action>
   - Check: <verification>
2. ...

### Files
- `<path>` - <why it matters>

### Risks
- <only real risks or unknowns>

Ready for approval.

## Stop Conditions
Stop and ask for clarification only if the requested goal is impossible to infer from the available task and code.

# Project

## Project Goal

[Describe the project’s primary outcome in one or two sentences.]

The system should:

- [Core behavior or capability 1]
- [Core behavior or capability 2]
- [Core behavior or capability 3]
- [Fallback, safety, or reliability behavior if applicable]

## Target Project

[Replace this line with the project root, repository location, workspace, or other target identifier required by the implementation.]

## Authoritative Supporting Documents

- `[DOCUMENT 1]`
- `[DOCUMENT 2]`
- `[DEVELOPMENT PLAN]`
- [Add or remove supporting documents as needed.]

These documents define the project requirements, implementation constraints, architecture decisions, and phased work plan.

## Existing Work

[Summarize what has already been implemented or verified.]

Do not reimplement completed work unless current validation demonstrates that it is incomplete, incorrect, or regressed.

## Remaining Work

Use the project’s development plan as the authoritative source for phase numbering, scope, acceptance criteria, and gates.

For each remaining phase, include:

### Phase [N] — [PHASE NAME]

[One-sentence description of the outcome this phase must achieve.]

Required behavior:

- [Requirement 1]
- [Requirement 2]
- [Requirement 3]
- [Requirement 4]
- [Add or remove requirements as needed.]

Repeat this structure for each remaining phase.

Do not invent additional phases unless they are required by the development plan or by newly discovered project requirements.

## Constraints

- Preserve verified working behavior unless a current requirement or regression requires a change.
- Use supported interfaces and documented integration points.
- Do not introduce unrelated features or architectural changes.
- Do not expose or log secrets, credentials, protected data, or sensitive source content.
- Do not trust unverified external or generated claims as proof.
- Enforce defined scope boundaries.
- Keep retries, loops, and automatic recovery bounded.
- Preserve path-independent or environment-independent behavior when required.
- Make focused changes and preserve unrelated work.
- [Additional project-specific constraint]
- [Additional project-specific constraint]

## Completion Criteria

The project is complete when:

- All required phases satisfy their acceptance criteria.
- All required deterministic tests or validation scenarios pass.
- Live, destructive, or environment-dependent tests remain explicitly controlled where applicable.
- Required documentation is sufficient for setup, operation, testing, and troubleshooting.
- No credential, sensitive-data, or unintended path leak exists.
- The full relevant validation suite passes.
- No known blocking defect remains.
- The final implementation matches the authoritative project documentation.

## Execution Guidance

Before beginning implementation:

1. Read this file and all authoritative supporting documents completely.
2. Determine the current verified state before changing code.
3. Identify the first incomplete or failed phase.
4. Work only on that phase unless a dependency requires a bounded prerequisite fix.
5. Validate the work against that phase’s acceptance criteria.
7. Do not mark work complete based only on historical claims.


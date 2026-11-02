# Self-contained PROJECT.md planning structure

Write the entire development plan inside PROJECT.md. No config.json is needed; do not request, generate, or modify configuration files. Do not create, request, link as a required dependency, or defer planning to a separate development-plan file. Keep the bundled template's eight top-level sections in order. Add the following detail as subsections within those sections; do not create a second planning artifact.

## Section placement

1. **Project Goal:** outcome, users, deliverables, scope exclusions, stable requirement IDs R1, R2, etc., and evidence sources. Distinguish explicit requirements from assumptions. Add architecture/interfaces as subsections: stack and rationale, components, contracts, data flow, persistence, errors, and relevant trust boundaries. Label unverified technical choices as proposals.
2. **Target Project:** root/repository or an explicit unresolved destination. Add environment/setup: supported environments, evidenced dependencies/versions, setup commands, configuration, and secret handling without secret values.
3. **Authoritative Supporting Documents:** list only real optional source documents. State that this PROJECT.md owns the full development plan and may stand alone. If no supporting documents exist, say so; do not ask for a separate plan.
4. **Existing Work:** inspected versus reported state, verified completed tasks/phases, relevant regression evidence. Never claim implementation or tests passed merely because planning is complete.
5. **Remaining Work:** dependency-ordered phase overview and the full phase/task details below. Use only necessary phases; include Phase 0 and Phase 1 for new projects, and preserve existing authoritative numbering where applicable.
6. **Constraints:** applicable template safeguards, scope boundaries, assumptions, decisions, blockers/readiness effects, specific risks, prevention, recovery, and bounded retries.
7. **Completion Criteria:** observable integrated acceptance, requirement coverage mapping, validation strategy/command registry, and final documentation/handoff. Map every requirement to its tasks and acceptance evidence. For each check specify its readable name, exact command where known, working directory, prerequisites, availability, expected result, and phase gates using it. Separate current automated checks from manual, environment-dependent, and planned checks. Document working-directory assumptions here; no runner configuration or separate command file is required.
8. **Execution Guidance:** read this PROJECT.md and any actual optional source documents, determine current state, work on the first incomplete phase, verify its gate, preserve evidence, and hand off artifacts and known limitations. Do not defer execution instructions to another plan.

## Pattern for every phase

Retain the template's Phase heading, measurable outcome, and Required behavior list. Add these fields to EVERY phase, including Phase 0 and Phase 1:
- **Status:** Planned / In progress / Blocked / Verified, with evidence. Default unimplemented work to Planned.
- **Requirements:** R IDs.
- **Entry criteria and dependencies:** required inputs, decisions, prerequisite task/phase gates.
- **In scope / Out of scope:** specific deliverables and change boundaries.
- **Tasks:** bounded actions with stable P0-T1, P1-T1, etc. IDs. Specify inputs, concrete changes/decisions, outputs, dependencies, and observable acceptance for each task. Split broad tasks into independently verifiable implementation-and-review units.
- **Validation:** relevant validation checks and exact commands where known, directory, prerequisites, expected results. Label future and manual/scenario checks explicitly.
- **Exit gate:** measurable acceptance and evidence required to advance.
- **Handoff:** artifacts, interface contracts, decisions, known risks.

Use the same pattern for all subsequent justified phases; avoid circular dependencies or unrelated platform work. Cover the entire requested project rather than just initial setup. Add accessibility, security, migration, performance, deployment, or rollback work only where relevant. Separate optional follow-ups from required scope.

If initial investigation verifies Phase 0, record it under Existing Work and keep only remaining phases under Remaining Work; do not omit its evidence. For an already established project, preserve historical phase numbering and avoid reimplementing verified work.

Replace all template placeholders with concrete content, explicit assumptions/open decisions, or Not applicable with a reason. A draft may have visible unresolved decisions; an implementation-ready plan must resolve blocking decisions. The final PROJECT.md must be usable without locating another plan.

# Validation guidance inside PROJECT.md

Document the validation strategy under Completion Criteria and the relevant phase gates inside PROJECT.md. Validation is part of the plan, not a separate configuration task.

## Evidence and availability

Inspect actual manifests, scripts, build/test configuration, CI, and available tooling when a repository is accessible. Match the project platform, package manager, interpreter and scripts. Include tests, lint, type checks, builds, schema checks or smoke scenarios only where relevant. Do not invent existing scripts, dependencies, flags, tests or successful results.

For an idea-only project, recommend a practical strategy and label tools/commands as Proposed or Planned. List the phase/task that establishes each check. Missing test infrastructure, config.json, or a runner is not a planning blocker. State when no checks are currently executable.

## Command registry in the document

For each check, document a readable name, exact executable/arguments where known, working directory, prerequisites, purpose, expected exit code/output, availability, and the phase/task gates using it. If the command is not yet knowable, specify its intended behavior and the task that finalizes it; do not present guessed commands as verified.

Separate current deterministic automated checks, future checks, manual/scenario checks, and environment-dependent checks. Match platform-specific executable names and document paths with spaces correctly. If a command relies on a particular directory, make that prerequisite explicit.

Cover normal behavior, relevant failure paths, regression preservation, and integrated completion. Make acceptance criteria observable rather than simply saying works correctly.

## Scope and reporting

Only execute already available, understood, bounded, local, non-destructive checks when useful. Exclude installation, deployment, publishing, destructive cleanup, live migrations, writing formatters, external communications, and credential-dependent/live tests from automatic initialization checks. Document controlled future checks as appropriate.

Report Passed, Failed, Blocked, or Not run with actual evidence. Never turn missing dependencies or future tests into a passing result. Do not alter product source, tests, or configuration merely to make initialization validation pass.

Do not request, create, read as a prerequisite, patch, or modify config.json. Do not emit validation_commands JSON, configuration diffs, runner settings, or separate validation files. The only planning output is PROJECT.md.

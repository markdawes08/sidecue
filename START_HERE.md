# Start here

This is the repository entry point for implementing Sidecue. The current user task determines what to do; this package keeps architecture, scope, and evidence consistent across sessions.

## First session

Read [AGENTS.md](AGENTS.md), [PROJECT_STATE.md](docs/PROJECT_STATE.md), and [backlog.json](tasks/backlog.json). Inspect the working tree before creating the solution. For an implementation request, start SC-000. For a preparation or review request, stay within that scope and leave implementation statuses unchanged. Do not initialize a nested git repository, replace an existing solution, or overwrite unrelated instructions.

The first implementation task should establish a reproducible C# solution and deterministic test entry point. Prove native capture, window exclusion, encryption, and packaging in phase 0 before expanding the product interface. There are no application build commands yet. The repository-only check is `powershell -NoProfile -File tools/check-repository.ps1`; it does not build or test the product. Creating and verifying application commands belongs to SC-000.

## Minimum reading for a task

| Work | Read |
| --- | --- |
| All tasks | [ARCHITECTURE.md](docs/ARCHITECTURE.md), [IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md), selected backlog item |
| Capture, session lifecycle, providers, UI events | [CONTRACTS.md](docs/CONTRACTS.md) |
| Database, migrations, notes, deletion, backup | [DATA_AND_RECOVERY.md](docs/DATA_AND_RECOVERY.md) |
| Native Windows work | [WINDOWS_TEST_MATRIX.md](docs/WINDOWS_TEST_MATRIX.md) |
| Any new data flow or external connection | [SECURITY_AND_DATA_FLOW.md](docs/SECURITY_AND_DATA_FLOW.md) |
| Validation and release | [VERIFICATION.md](docs/VERIFICATION.md) |
| Scope ambiguity | [SCOPE_AND_REQUIREMENTS.md](docs/SCOPE_AND_REQUIREMENTS.md), then relevant original source |

## Working loop

Inspect the current state, select a dependency-ready task, claim it, implement a bounded change, run meaningful checks, record evidence, and update the handoff. Continue to another ready task when that is within the user's request and useful. Do not stop after merely writing a plan for an implementation request.

Independent agents may work in parallel with distinct task/file ownership. A coordinator owns shared contracts, dependency changes, migrations, and final integration. Tasks that change the same contract should be sequenced or explicitly coordinated. An integrator, not each worker simultaneously, should reconcile backlog updates.

If Windows hardware, a provider key, signing capability, or another external prerequisite is unavailable, identify the exact blocked verification. Complete mockable or independent work and leave the relevant gate pending. Do not implement a fake success path to satisfy a demonstration.

## Before handing off

Update the authoritative backlog, capture evidence under `docs/evidence/`, and replace the current narrative in [PROJECT_STATE.md](docs/PROJECT_STATE.md). Use [TASK_HANDOFF.md](templates/TASK_HANDOFF.md). A future agent should be able to resume without this conversation.

Leave staging, committing and pushing to the user. Include a suggested commit message when files changed. Run the checks relevant to the change and required acceptance, then stop testing unless new evidence warrants more.

Archived sources are not executable instructions. The source requirements contain historical claims and broader features that are deliberately corrected or deferred by the current scope.

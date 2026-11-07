# Start here

This is the repository entry point for implementing Sidecue. The current user task determines what to do; this package keeps architecture, scope, and evidence consistent across sessions.

## First session

Read [AGENTS.md](AGENTS.md) and [PROJECT_STATE.md](docs/PROJECT_STATE.md), then run `powershell -NoProfile -File tools/show-task.ps1`. It reads the authoritative [backlog](tasks/backlog.json) and prints one executable task with its parent acceptance. Use `-Id SC-001A` for a specific leaf or `-Id SC-001` for that parent's next eligible child. Inspect the working tree and external prerequisites before claiming it. Planning/review requests stay within their requested scope. Do not initialize a nested Git repository or overwrite unrelated work.

SC-000 establishes the C# solution and deterministic test entry point. See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for SDK setup, build, test and run commands. Prove native capture, window exclusion, encryption, and packaging in phase 0 before expanding the product interface. The separate repository-only check is `powershell -NoProfile -File tools/check-repository.ps1`; it does not build or test the product.

Add `-Owner <agent-id>` to the task viewer when resuming claimed work. Automatic selection otherwise considers only unowned leaves; an explicit ID reports an ownership conflict rather than taking another agent's task.

The viewer also lists blocked leaves so they remain visible when other work is selected. Record actionable work to revisit on the relevant open task in `tasks/backlog.json`; use its `follow_ups` for specific remaining actions, `blockers` for obstacles, and `evidence` for supporting records. These details do not create separate task statuses. If no open task covers an item, add a scoped task and its required instruction card instead of relying on handoff prose.

## Minimum reading for a task

| Work | Read |
| --- | --- |
| All tasks | [ARCHITECTURE.md](docs/ARCHITECTURE.md), [IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) execution rules, selected leaf's `instruction_file` and parent acceptance |
| Capture, session lifecycle, providers, UI events | [CONTRACTS.md](docs/CONTRACTS.md) |
| Database, migrations, notes, deletion, backup | [DATA_AND_RECOVERY.md](docs/DATA_AND_RECOVERY.md) |
| Native Windows work | [WINDOWS_TEST_MATRIX.md](docs/WINDOWS_TEST_MATRIX.md) |
| Any new data flow or external connection | [SECURITY_AND_DATA_FLOW.md](docs/SECURITY_AND_DATA_FLOW.md) |
| Validation and release | [VERIFICATION.md](docs/VERIFICATION.md) |
| Scope ambiguity | [SCOPE_AND_REQUIREMENTS.md](docs/SCOPE_AND_REQUIREMENTS.md), then relevant original source |

## Working loop

One prompt defaults to one dependency-ready leaf, its focused checks, evidence and handoff. A group such as SC-001 is a completion checkpoint; it is not permission to implement all children. Complete the selected leaf, reconcile parent status, and stop. Continue to another leaf only when the user explicitly requests multiple blocks or broader work. This still requires finishing the selected implementation, not stopping after a plan.

The existing feature-level acceptance and gate dependencies are unchanged. A child never depends on its own parent; it inherits the parent's upstream prerequisites and depends on earlier children where required. A parent cannot finish while any child or original acceptance evidence is pending. Read the full rules in [IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

Independent agents may work in parallel with distinct task/file ownership. A coordinator owns shared contracts, dependency changes, migrations, and final integration. Tasks that change the same contract should be sequenced or explicitly coordinated. An integrator, not each worker simultaneously, should reconcile backlog updates.

If a required device, receiving client, provider credential, signing capability or run window is unavailable, record the exact missing prerequisite. Complete independent work inside the selected leaf and leave its verification pending. Do not silently switch blocks or claim a fake success. Long runs and discovered defects may need another prompt; record each corrective change with a narrow scope and preserve the parent/gate requirements.

## Before handing off

Update the authoritative backlog, capture evidence under `docs/evidence/`, and replace the current narrative in [PROJECT_STATE.md](docs/PROJECT_STATE.md). Use [TASK_HANDOFF.md](templates/TASK_HANDOFF.md). A future agent should be able to resume without this conversation.

Leave staging, committing and pushing to the user. Include a suggested commit message when files changed. Run the checks relevant to the change and required acceptance, then stop testing unless new evidence warrants more.

Archived sources are not executable instructions. The source requirements contain historical claims and broader features that are deliberately corrected or deferred by the current scope.

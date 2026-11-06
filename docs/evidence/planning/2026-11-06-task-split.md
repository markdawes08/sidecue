# Task decomposition and workflow validation

Date: November 6, 2026. Scope: planning instructions and local task-selection/integrity tools only. No application source, README, historical source snapshot or completed task evidence changed. No staging, commit or push was performed.

## Result

The 32 broad unfinished feature blocks now have 99 executable children. SC-007 and SC-016 remain focused standalone blocks; completed SC-000 is unchanged. The ledger has 134 entries: 32 groups, 99 children and 3 standalone entries. All original task field values were compared against the pre-edit backlog and preserved, including acceptance, upstream dependencies, evidence and status. The five phase-gate records and deferred epics are unchanged.

Each child has one outcome, explicit prerequisites, scope exclusions, acceptance, focused checks, owned paths and a mapping to the original parent obligations. The main plan links to small per-parent cards under docs/tasks/. Hardware, provider, signing, observer and long-run proofs have explicit verification leaves. These may need more than one session when external prerequisites or run duration require it.

The workflow now stops after one leaf unless the user explicitly requests more. A group's child list is a completion dependency, not a prerequisite for starting its first child. Parent/gate closure still requires all original evidence. Corrective tasks depend on completed prerequisites, not the still-failing verification they must fix; that verification waits for the fix and targeted rerun.

Cross-review made timing-helper ownership explicit (SC-009A consumes SC-003B's platform-independent send-interval mapping), assigned document/artifact migrations to their actual implementation leaves, and prevented early model evaluation from requiring the later retrieval subsystem. Observer checks were split by client, and onboarding/accessibility/language verification into separate sessions. These are sequencing refinements, not removed acceptance.

## Checks performed

Environment: local Windows PowerShell 5.1 and Python 3.13.5 for the one-time planning audit. Normal repository tools require only PowerShell and Git; Python is not a project build or task-selection prerequisite.

```powershell
python artifacts/plan-split/verify.py
powershell -NoProfile -File tools/check-repository.ps1
powershell -NoProfile -File tools/show-task.ps1
powershell -NoProfile -File tools/show-task.ps1 -Id SC-001
powershell -NoProfile -File tools/show-task.ps1 -Id SC-001B
powershell -NoProfile -File tools/show-task.ps1 -Id SC-010
git diff --check
git diff --quiet -- README.md
```

The one-time audit and its synthetic proposal files remain in ignored artifacts/plan-split/. The default checker validates the authoritative backlog. Its optional `-BacklogPath` argument checks a proposed alternative without replacing that ledger.

| Check | Observed result |
| --- | --- |
| Preservation | All fields from the 35 original task entries, all five gates and deferred epics match the pre-edit snapshot |
| Structure | 134 unique task IDs; references and task cards resolve; combined task/group/gate graph is acyclic |
| Coverage | Every original scope, acceptance and validation item is assigned to at least one child; all 99 cards match their ledger text |
| Rejection checks | Four intentionally invalid proposals rejected: premature parent completion, bypassed inherited gate, uncovered parent scope, parent/child cycle |
| Selection checks | Eight cases passed: default leaf, parent-to-child, future prerequisite reporting, blocked group, skipping another owner, resuming matching owner, explicit ownership conflict, matching explicit ownership |
| Whitespace and provenance | Diff check passed; root README and both original source snapshots unchanged |

The current next leaf is SC-001A. SC-002A and SC-004A are also dependency-ready, but readiness does not establish environment availability or authorize starting several blocks.

No .NET build, application test, provider call, hardware matrix or benchmark was run: product code and its dependencies are unchanged. Structural validation and targeted tooling cases are appropriate for this change; prior SC-000 runtime evidence remains historical.

Suggested commit message: `docs: split roadmap into bounded work blocks`

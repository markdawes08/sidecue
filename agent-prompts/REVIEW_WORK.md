# Review Sidecue work

Review the selected task/change against AGENTS.md, docs/SCOPE_AND_REQUIREMENTS.md, docs/ARCHITECTURE.md, the relevant contracts, and its backlog acceptance criteria. Inspect the actual diff and evidence. Do not assume passing checks from an agent summary, and do not change unrelated files during review.

Use tools/show-task.ps1 -Id to inspect the selected leaf and parent. Check that the change stayed inside the leaf boundary, inherited prerequisites/gates remain intact, and parent completion is not claimed while children or original evidence are pending. Scope and acceptance coverage numbers refer to the parent's ordered backlog arrays, starting at 1.

Focus on behavior and regressions: source/timestamp identity; transcript revisions and durable writes; gaps; stale generation events; exact pause/clear/end behavior; plaintext leakage; cancellation; disposal; migrations; installer/data separation; and unsupported native compatibility claims.

Run appropriate available checks. A fake provider or unit test cannot establish actual capture exclusion, audio-device behavior, external provider quality, or signed-update trust. Verify those claims against the recorded environment and artifacts.

Report findings with severity, path/location, concrete failure scenario, and the needed correction. Separate blockers from optional improvements. State what was not tested. If no material issue is found, say so while retaining any unverified acceptance conditions. Do not mark a task/gate complete unless its required evidence exists.

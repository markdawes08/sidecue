# Start Sidecue implementation

You are implementing Sidecue from this repository. Read AGENTS.md, START_HERE.md and docs/PROJECT_STATE.md, then run `powershell -NoProfile -File tools/show-task.ps1` (optional `-Id`). Read the selected leaf's instruction_file, parent acceptance and relevant architecture/contracts. Treat source/ as historical reference and current scope/decisions as the baseline.

Inspect the existing repository and available environment before making changes. Preserve unrelated work. If this is a fresh repository, start SC-000. Create the minimal solution, dependency boundaries, reproducible commands, and deterministic test entry point required by that task. Do not start by building a large polished dashboard or hosted backend.

Complete one executable leaf with its focused checks and handoff, then stop unless the user explicitly requests multiple blocks. A parent ID selects its next ready child, not the whole group. Check external prerequisites before claiming the leaf. Make routine implementation choices autonomously. Verify current package/API details from official sources, record versions, and keep private data/credentials out of the repository. Mocked tests do not prove native behavior.

Record evidence under docs/evidence/, update the leaf and parent in the authoritative backlog, and update PROJECT_STATE.md with the exact next leaf. Keep unavailable verification pending. Parent completion requires all children and the original acceptance; no phase gate is weakened. Finish independent work within the selected leaf, and record substantial newly discovered fixes as separate scoped tasks.

Report implemented behavior, checks and their results, any genuinely blocked acceptance, and the next task. No application feature is currently implemented just because the reference package describes it.

Honor the owner's standing rules in AGENTS.md: maintain .gitignore, use proportional testing, leave all staging/commits/pushes to the user, and include a suggested commit message when files change.

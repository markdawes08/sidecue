# Continue Sidecue implementation

Read AGENTS.md, START_HERE.md and docs/PROJECT_STATE.md. Run `powershell -NoProfile -File tools/show-task.ps1` (or add `-Id` for the requested task). Inspect the working tree and latest evidence. Continue the assigned leaf, otherwise select one dependency-ready leaf; a parent ID means its next eligible child. Read its instruction_file, parent acceptance, relevant contracts and external prerequisites before claiming ownership.

Implement and validate the next bounded change. Preserve existing user work and coordinate shared files with other agents. Do not reopen settled architecture without concrete evidence, expand into deferred features, or replace a missing capability with a success-shaped stub.

Complete this leaf's implementation and focused checks, then stop unless the user explicitly requested multiple blocks. Use actual results to update the leaf and parent in the backlog and write a short handoff. Parent completion requires every child plus the original acceptance evidence. If a required check cannot run, record the missing prerequisite and keep affected status/gates pending. Put unrelated or substantial newly found defects into scoped corrective tasks rather than expanding the prompt indefinitely. Finish with changes, results, limitations and the exact next leaf.

Honor the owner's standing rules in AGENTS.md: maintain .gitignore, use proportional testing, leave all staging/commits/pushes to the user, and include a suggested commit message when files change.

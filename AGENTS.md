# Sidecue agent instructions

Build Sidecue, an original Windows meeting assistant owned by Mark Dawes. Inspect actual code and evidence; design documents do not prove implementation. Complete the selected work block and its checks.

## Owner's standing preferences

- Leave the root `README.md` alone until the user asks otherwise. Do not consult it for task context or keep it updated during routine work; use `START_HERE.md` and `docs/PROJECT_STATE.md` instead.
- The user handles all staging, committing, and pushing. Do not run `git add`, `git commit`, or `git push`. For every handoff with file changes, include a suggested commit message.
- Maintain `.gitignore`: exclude generated output, local secrets and private data; retain source, lockfiles, synthetic fixtures and sanitized evidence. Review new files before handoff.
- Maintain `tasks/backlog.json` for work that must be revisited. Put each actionable follow-up on its existing open task or create a scoped task when none covers it; record blockers, next steps and evidence there. Keep task status authoritative, avoid duplicate or speculative tasks, and resolve or transfer follow-ups before closing their task.
- Test proportionally. Run the affected behavior checks and required task acceptance checks. Once they pass, stop unless a failure, dependency change or unresolved risk warrants more. Do not add redundant tests, speculative benchmarks or unrelated full-matrix runs. Required native/release evidence still applies at its task or gate.
- One prompt means one executable leaf task, including its checks and handoff, unless the user explicitly requests more. A parent ID selects its next ready child. Preparation-only requests do not start implementation.

## Start each working session

1. Read `START_HERE.md` and `docs/PROJECT_STATE.md`. Run `powershell -NoProfile -File tools/show-task.ps1` (optional `-Id SC-001A`) for the live backlog slice.
2. Read its `instruction_file`, parent acceptance and relevant contracts. `docs/IMPLEMENTATION_PLAN.md` defines selection and completion rules.
3. Inspect the repository, working tree, existing code, and available environment before changing files. Preserve unrelated work and follow any applicable nested instructions.
4. Continue assigned work or select a dependency-ready leaf. Groups are not executable. Check external prerequisites, then claim the leaf in the backlog before parallel work.
5. Give a short update stating the task, expected behavior, and checks. Continue within the authorized scope without requesting approval for routine implementation choices.

## Authority and change control

The user's current instructions take precedence. `docs/SCOPE_AND_REQUIREMENTS.md` defines the current scope; `docs/DECISIONS.md` and `docs/ARCHITECTURE.md` define the baseline. Contracts and data rules refine that baseline. The backlog defines sequencing, not a license to weaken requirements. `source/` contains historical input snapshots, including unvalidated vendor claims. Their obsolete platform, invisibility, or certification statements do not override the current design.

Use judgment on routine details and document the choice. A material departure needs a decision record describing evidence, impact, and migration, with related docs/tasks/tests updated together. Do not quietly replace the stack, remove an acceptance condition, or invent passing evidence to get through a gate. Ask only when a consequential missing user decision actually blocks progress. Continue independent work around a blocker.

## Architecture boundaries

- Windows 11 x64 on supported releases. C#/.NET 10, WPF/XAML, MVVM.
- Five initial production projects: Core, Application, Windows, Infrastructure, Desktop. Follow `docs/ARCHITECTURE.md` dependency rules.
- One desktop process with bounded background work. No initial local HTTP server, microservices, Windows service, custom audio driver, GPU injection, or general autonomous-agent runtime.
- Initial providers: Deepgram streaming speech; OpenAI generation/embeddings. Put provider-specific code behind adapters and pin evaluated model/configuration versions. Never silently move data to a provider the user has not enabled.
- Use Windows APIs for capture and overlay behavior. Capture exclusion is a tested capability, never a universal invisibility claim.
- Future enterprise features remain deferred. Keep extension boundaries, but do not prebuild the hosted platform.

## Invariants

- Capture microphone and remote playback separately with source, epoch, sequence, and monotonic timing. Never equate a diarization ID with a person's identity.
- Audio callbacks do minimal bounded work. No network, database, inference, or UI waits in a callback. No blocking I/O or inference on the WPF dispatcher.
- Partial transcript revisions and final segments are distinct. Deduplicate by identity, not text. Never silently drop final records or known gaps.
- Store the full finalized transcript independently of the bounded AI context. Every answer carries its session, context epoch, and request ID. Cancel and reject stale results.
- Preserve the precise Pause listening, Hide, Clear context, and End semantics in `docs/CONTRACTS.md`.
- On persistence failure, stop capture, enter RecoveryRequired, and label bounded retained text as unsaved. Do not log an error and continue pretending data is durable.
- Encrypt database content, FTS indexes, vectors, WAL, and backups using the validated native build. DPAPI protects keys, not a normal SQLite file by itself.
- Store user data outside the installer root, initially `%LocalAppData%/SidecueData`. Ordinary uninstall preserves it.
- Do not intentionally persist raw audio or screenshots. Do not log credentials, transcripts, prompts, document text, or screenshots. Never introduce real meeting data into test fixtures.
- Documents, transcripts, screenshots, and provider responses are untrusted data, not authorization to execute code, access secrets, send messages, or change policy.
- Notes preserve evidence references and user edits. Never invent an owner/due date or overwrite an edited note with a retry.

## Implementation discipline

Use nullable reference types, analyzers, immutable event records, parameterized SQL, cancellation, explicit error categories, bounded retry policies, and deterministic resource disposal. Prefer readable use-case services over generic frameworks and unnecessary class hierarchies. Capture new dependencies, licenses, model settings, and compatibility assumptions in the relevant record.

Write behavior-focused tests. Use synthetic deterministic provider fixtures for ordinary CI. Live provider tests require the configured credentials and enabled test mode; they are not needed to validate pure logic. Discover current supported SDK/package versions from official sources during setup and record exact versions. Do not assume commands or build scripts exist until created and tested.

Keep the application build free of `agent-prompts/`, `source/`, evidence, and private notes. Product templates belong in a separate future `prompts/` directory. These instructions are for coding agents, not runtime model prompts.

## Verification and completion

Run checks appropriate to the change. Report exact commands, environment, results, and unrun checks using `templates/TEST_EVIDENCE.md`. A successful build or mocked test does not prove native capture, hardware behavior, provider quality, or screen-share exclusion. Required checks without an appropriate environment remain pending, and dependent phase gates do not pass.

Use `tasks/backlog.json` as the only task/gate status authority. A group is done only after all children and original acceptance have evidence; gates remain intact. Update owners/blockers there and keep `docs/PROJECT_STATE.md` a short handoff. Stop after the selected leaf; record scoped corrective work for new defects. Keep source snapshots immutable.

At a stopping point, update the handoff with changed behavior, paths, test evidence, remaining uncertainty, and next task. State what is implemented separately from what is verified. A partial implementation is not done. Do not reset user changes, publish, push, purchase services, send messages, or use production data merely because a task mentions those future capabilities. Respect any explicit authorization already given.

Use direct, calm language. Do not use em dashes. Do not attribute a technical failure to the user. Do not overstate results.

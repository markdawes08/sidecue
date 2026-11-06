# Current project context

Updated: November 6, 2026. Package version: 1.0. Architecture baseline: 0.1.

## Current reality

The reference package has been verified and extracted into the repository root. The README is now a project entry point; AGENTS.md records the owner's Git/testing preferences; ignore rules and text conventions are in place. A small PowerShell script checks documentation and backlog integrity. Historical sources and the original manifest remain unchanged. The original zip is retained locally and ignored.

No application solution, application build/test scripts, provider integration, Windows prototype, or completed runtime test exists. This session was repository preparation only. Evidence is in [the preparation record](evidence/repository-preparation/2026-11-06.md).

Task and gate status belongs only in [backlog.json](../tasks/backlog.json). Do not copy the status table here. A fresh implementation starts with SC-000 and proceeds according to dependencies and gates in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

## Context needed by the next agent

- User wants an original project implemented primarily through Codex agents.
- Windows compatibility is the current focus. The native C#/.NET/WPF stack was deliberately selected after reviewing other implementations.
- Full source requirements include broad enterprise features and technical claims that are corrected or deferred in [SCOPE_AND_REQUIREMENTS.md](SCOPE_AND_REQUIREMENTS.md).
- Use cloud providers initially, behind adapters. Local persistence/capture is not a claim of fully local inference.
- The owner prefers practical, autonomous progress and direct explanations without em dashes or blame-oriented wording.
- The user performs all staging, commits and pushes. Always supply a suggested commit message when files change. Maintain `.gitignore` as new tooling is introduced.
- Use focused tests and required task/gate evidence. Stop after relevant checks pass unless changes, failures or unresolved risks justify more. Keep handoffs concrete enough for smaller models to resume without the chat history.
- No repository remote, private account configuration, signing identity, API credentials, or real meeting data is supplied by this package.

## Environment and command inventory

Observed locally on November 6, 2026: Windows x64 build 26200; Windows PowerShell 5.1.26100.9711; Git 2.55.0; .NET SDK 10.0.301 with MSBuild 18.6.4 and .NET/WindowsDesktop runtime 10.0.9. Windows Kits include directories exist for 10.0.19041.0, 10.0.22621.0, 10.0.26100.0 and 10.0.28000.0. Directory presence is not a toolchain compatibility test. Exact support status, SDK/package pins and restore/build compatibility must be checked during SC-000; nothing is pinned yet.

Repository check: `powershell -NoProfile -File tools/check-repository.ps1` from the root. It validates JSON, task/gate references and cycles, task-card presence, inline local Markdown file links and original source hashes. It does not validate Markdown anchors, external URLs, application behavior or fixture outcomes.

No application build, test, package, or run command has been verified. Missing Windows hardware checks remain explicit limitations, even if pure tests can run elsewhere.

## Current unknowns

The tested SQLCipher distribution, exact provider/model pins, capture compatibility matrix, packaging/signing capability, and measured latency are pending implementation evidence. These are listed in [DECISIONS.md](DECISIONS.md) and scheduled in the backlog. Do not use dummy values that look production-ready.

## Next concrete action

When asked to begin implementation, select SC-000 in [the backlog](../tasks/backlog.json), read its [task card](IMPLEMENTATION_PLAN.md#sc-000), and recheck the working tree. Create the five-project solution, minimal WPF shell, xUnit projects, reviewed SDK/package pins, build/test PowerShell commands and Windows CI. Verify the task's clean-checkout acceptance and document actual commands/results. Keep that slice bounded; later tasks own meeting features and native proofs. No task or gate was advanced by this preparation.

## Handoff maintenance

Replace this narrative as work progresses: describe actual implemented behavior, integration state, blockers, verified commands, and the next concrete action. Link the most recent task evidence and handoff. Keep old task-specific records under `docs/evidence/` instead of appending a growing diary here.

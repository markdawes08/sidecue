# Current project context

Updated: November 6, 2026. Architecture baseline: 0.1.

## Current reality

Sidecue has a five-project .NET solution and a minimal WPF window. The window states that meeting features are not available yet. It performs no audio capture, persistence or provider calls. Core, Application, Windows and Infrastructure currently establish dependency boundaries without placeholder meeting logic.

SDK/package pins, lockfiles, recommended analyzers with warnings as errors, PowerShell setup/build/test/run commands and Windows CI configuration are present. The immutable shell view model needs no MVVM toolkit package yet; add the selected toolkit when mutable UI behavior needs it.

Task and gate status belongs only in [backlog.json](../tasks/backlog.json). The latest implementation evidence is [SC-000](evidence/SC-000/2026-11-06.md), including exact source hashes, commands, corrected failures and limitations. Historical package provenance and source snapshots remain unchanged.

## Standing owner preferences

- Leave the root README alone until the user says otherwise. Use START_HERE.md, this handoff and task documents for context.
- The user handles all staging, committing and pushing. Include a suggested commit message when files change.
- Maintain .gitignore. Generated output, downloaded SDKs, private data and secrets stay out of Git; source, lockfiles, synthetic fixtures and sanitized evidence stay reviewable.
- Test the affected behavior and required task acceptance. Once relevant checks pass, expand only for a failure, dependency change or unresolved concern.
- Keep changes bounded and handoffs concrete enough for smaller models to resume without the chat. Do not confuse a shell, build or mock test with implemented meeting features.

## Verified commands and environment

See [DEVELOPMENT.md](DEVELOPMENT.md) for setup, commands and dependency updates; see [DEPENDENCIES.md](DEPENDENCIES.md) for official sources and version/license notes.

```powershell
# Optional, if the exact SDK is not on PATH:
powershell -NoProfile -File tools/setup-sdk.ps1

# Locked restore, Release build and deterministic tests:
powershell -NoProfile -File tools/test.ps1

# Open the minimal shell after a Debug build:
powershell -NoProfile -File tools/run.ps1
```

Local validation used Windows 11 Home 25H2, build 26200.9711, x64, PowerShell 5.1, SDK 10.0.403 and runtime 10.0.14. The system SDK remains 10.0.301; tooling uses the verified local SDK under ignored artifacts/dotnet. No machine-wide installation or persistent PATH change was made.

A source-only export with fresh NuGet/CLI caches restored locked dependencies and built with zero warnings/errors. All eight tests passed: seven architecture cases and one WPF XAML/binding/layout case. The actual Release executable opened and closed normally. The run script invokes the same build/launch tools; no separate visual UX acceptance is claimed.

The Windows CI workflow is checked in for the user's review, targets windows-2025, and records the actual runner image. Hosted execution is pending the user's push. The clean source export was local, not a fresh OS installation or a new Git commit.

## Next concrete action

Select SC-001 in the backlog and read its [task card](IMPLEMENTATION_PLAN.md#sc-001), CONTRACTS.md and the relevant verification cases. Claim it, implement the smallest typed records/interfaces and deterministic replay/fakes, then prove identity, ordering, epoch and stale-result behavior with synthetic events. Keep actual native capture, encryption and provider work in their owning tasks.

Exact SQLCipher selection, provider/model pins, supported hardware/share scenarios, signed packaging and measured latency remain unresolved. No phase gate is closed by the bootstrap. Keep the personal Windows workflow ahead of hosted/team features and preserve the existing data boundaries.

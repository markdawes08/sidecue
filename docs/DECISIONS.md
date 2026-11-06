# Architecture decision register

Status applies to a design decision, not to implementation or test completion. The user asked us to choose the architecture and prepare this coding package; the following baseline guides implementation. Use [DECISION_RECORD.md](../templates/DECISION_RECORD.md) for a material revision. Record related backlog IDs and test evidence. Do not silently update history.

| ID | Status | Decision | Consequence |
| --- | --- | --- | --- |
| ADR-001 | Baseline | Windows 11 x64; C#/.NET 10; WPF/MVVM | Other platforms need separate capture/UI work; do not build them now |
| ADR-002 | Baseline | Modular application, one process initially | Use boundaries/queues first; add process isolation only for a demonstrated need |
| ADR-003 | Baseline | Separate mic and playback streams | Preserve source, epoch, timing, gaps, and anonymous diarization |
| ADR-004 | Baseline | Replaceable providers; versioned model configuration | No SDK types in domain records, no silent vendor fallback |
| ADR-005 | Baseline | Durable finalized transcript independent of prompt context | Bounded live requests do not discard meeting history |
| ADR-006 | Baseline with native-build gate | SQLCipher-backed SQLite and DPAPI-protected keys | Prove encryption, FTS, migrations, durability, and packaging before product use |
| ADR-007 | Baseline with empirical gate | Documented Windows capture exclusion | Test supported capture paths; no universal invisibility claim |
| ADR-008 | Baseline | Hybrid retrieval with evidence and index generations | Preserve original locations and prevent incompatible vector mixing |
| ADR-009 | Baseline | Explicit actions, cancellation, no general runtime agent | Context is data; sending/automation permissions cannot come from model output |
| ADR-010 | Baseline | Personal desktop before hosted/team product | Defer enterprise infrastructure while retaining clean boundaries |
| ADR-011 | Baseline clarification | User state outside installer RootAppDir | Updates/uninstall cannot unintentionally remove the meeting database/key |
| ADR-012 | Baseline clarification | Native/provider checks require real evidence | Mock tests do not close Windows or quality gates |

## Selections to resolve during implementation

These are expected engineering work, not reasons to stop and ask routine confirmation.

| Selection | Required evidence | Resolution point |
| --- | --- | --- |
| Exact SDK/package pins and Windows build matrix | Supported official releases, reproducible restore/build, dependency notices | Bootstrap and phase-0 tasks |
| SQLCipher distribution | Native version, encryption/FTS tests, redistribution suitability, update provenance | Encryption proof; keep dependent data features blocked if unresolved |
| Live/detailed model IDs and STT configuration | Repeatable latency/quality results, enabled provider capabilities | Provider evaluation tasks |
| Native audio wrapper gaps | Working process/endpoint capture, device transitions, source timing | Capture proof |
| UI styling library or custom resources | WPF accessibility, focus, DPI, and overlay behavior | Minimal shell then UX task |
| Signing setup | Trusted release signing and verification evidence | Release engineering; do not invent or commit credentials |
| Product source license | Owner's selected distribution intent | Before any public source release; does not block local implementation |

The owner has not asked this package to publish a repository, purchase infrastructure, or choose a legal distribution license. Complete local preparation and keep any genuinely necessary external decision concrete.

## Research record

The pinned comparison revisions and official references are in [Architecture_v0.1.txt](../source/Architecture_v0.1.txt). They explain the design, not authorization to copy another project's source. Recheck temporal API/package claims when implementing. Record the URLs, access date, actual selected versions, and tested compatibility in the task evidence.

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
| [ADR-013](#adr-013-local-transcript-storage-development) | Adopted | Build the transcript schema and writer against the reviewed local encrypted store | Full SC-004 evidence is required at SC-008C |
| [ADR-014](#adr-014-local-screenshot-prototype-development) | Adopted | Develop the screenshot prototype with the local window policy | Full SC-002 evidence is required at SC-005C |

## ADR-013 Local transcript storage development

Adopted November 27, 2026. SC-008 originally required all of SC-004 before any schema work could start. The reviewed SQLCipher binary and DPAPI-backed store already passed local restart, live-WAL and interrupted migration checks on Windows. These results support development against a disposable database containing synthetic records. [Storage adapter evidence](evidence/SC-004B/2026-11-09.md) and [durability preflight](evidence/SC-004C/2026-11-10-preflight.md)

SC-008A and then SC-008B may use that exact binary for local development and focused tests. SC-008C requires full SC-004 completion, including the independent clean Windows run. A failure there must be resolved before SC-008 can close. If it requires a different binary or a change to the store, repeat the affected schema and writer checks against the corrected configuration. Packaging still requires SC-004 and live integration still requires SC-008 and G0.

## ADR-014 Local screenshot prototype development

Adopted November 27, 2026. SC-002B established the local window policy and SC-002G exercised real foreground retention with a synthetic target. SC-005D can use synthetic pixels to prove preview consent and frame ownership. The selected-target prototype and resource handling can then be developed locally against the implemented window policy. [Window policy evidence](evidence/SC-002B/2026-11-06.md) · [Foreground evidence](evidence/SC-002G/2026-11-27.md)

SC-005E exercises picker dismissal from the local prototype on one display. SC-005C still requires all of SC-002 and the physical screenshot matrix. SC-005 cannot close until that evidence exists. If overlay verification changes the window policy, repeat the affected preview and picker checks with the corrected policy. Request-scoped screenshot integration still belongs to SC-023 and its existing prerequisites.

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

SC-000's reviewed build/test versions and exact CI pins are recorded in [DEPENDENCIES.md](DEPENDENCIES.md). Native/provider selections remain with their owning tasks. The initial static shell postpones the MVVM toolkit dependency until changing UI state needs it; the selected architecture is unchanged.

The pinned comparison revisions and official references are in [Architecture_v0.1.txt](../source/Architecture_v0.1.txt). They explain the design, not authorization to copy another project's source. Recheck temporal API/package claims when implementing. Record the URLs, access date, actual selected versions, and tested compatibility in the task evidence.

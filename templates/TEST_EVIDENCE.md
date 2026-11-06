# Test evidence record

Copy this template to an evidence record for one run or a clearly identified set of related cases. Replace placeholders with observed values. Do not mark a case passed before running it. Remove secrets and personal meeting content from attached artifacts.

## Identity

| Field | Value |
|---|---|
| Evidence ID | `<unique run ID>` |
| Task ID and case IDs | `<SC task and V/CAP/AUD/WIN/PKG/SOAK cases>` |
| Date and time with offset | `<ISO 8601 timestamp>` |
| Tester or automation | `<name or runner identifier>` |
| Result | `not-run` |
| Code commit | `<full commit SHA, or uncommitted with diff hash>` |
| Working tree changes | `<clean, or exact relevant uncommitted files/diff artifact>` |
| Build/package hash | `<SHA-256 of tested binary/package>` |
| Architecture/contract version | `<document revision>` |

## Environment and configuration

| Field | Value |
|---|---|
| Machine/environment ID | `<stable non-sensitive label>` |
| OS edition, version, build, patch | `<actual values>` |
| CPU, RAM, GPU and driver | `<actual values>` |
| SDK/runtime and native library versions | `<dotnet, SQLCipher, underlying SQLite, relevant dependencies>` |
| Microphone/playback endpoints and drivers | `<IDs or redacted stable labels; headset/speakerphone; formats>` |
| Monitor layout and scaling | `<identity, resolution, DPI/scaling, HDR>` |
| Meeting client or browser version | `<actual version>` |
| Sharing mode and receiving client | `<window/monitor/tab; receiving observation details>` |
| Provider/model or fake fixture | `<IDs, model versions, capability/configuration hash; no keys>` |
| Prompt/profile/index versions | `<relevant IDs and hashes>` |
| Network conditions | `<connection, measured latency if available, injected failures>` |
| Synthetic fixture IDs/hashes | `<inputs and expected-result versions>` |
| Warm/cold state | `<how established>` |

## Procedure

Preconditions: `<what actually existed before the run>`

Exact command(s), shell, working directory, exit code, and log artifact:

```text
<paste commands as executed, with secrets removed and redactions noted>
```

For interactive cases, record the exact ordered actions, timing of injected faults, expected observations, and receiving-client observations. A local screenshot is not receiving-stream evidence.

## Results

| Case/variant | Expected behavior | Observed behavior | Result | Evidence artifact |
|---|---|---|---|---|
| `<case>` | `<assertion>` | `<actual observation>` | `not-run` | `<relative path and hash>` |

Use only `pass`, `fail`, `blocked`, `not-run`, or justified `not-applicable`. Describe a partial run explicitly; do not summarize unexecuted variants as passed.

For performance runs, include sample count, p50/p95, errors/timeouts, queue peaks, memory trend, and stage definitions. Keep failed/time-out samples visible in the report. State which targets are still unmeasured.

## Failure or limitation

- Failure/blocker: `<precise observation or unavailable environment>`
- Scope affected: `<task acceptance criterion, gate, compatibility claim>`
- Reproduction: `<minimal sequence or fixture>`
- Next action: `<concrete corrective or verification step>`
- Linked corrective task: `<ID if created>`

## Artifacts and handling

List artifact paths and hashes. Use synthetic data for captures, transcripts, logs, and request-body fixtures. Do not attach credentials, raw private meetings, full-memory dumps, or personal screenshots. If required evidence cannot be shared safely, describe its restricted location and provide a redacted summary without claiming the original was public.

## Completion assessment

- Acceptance criteria satisfied by this run: `<specific criteria>`
- Criteria still pending: `<specific criteria or none>`
- Code changes after this run: `<none, or why a rerun is required>`
- Backlog update: `<task/gate entry updated by the coordinating agent>`
- Reviewer observation, if applicable: `<reviewer and scope; review is not a substitute for execution>`

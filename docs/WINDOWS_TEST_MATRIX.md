# Windows test matrix

Initial status: **all cases are not-run**. This matrix is a test specification. Preparing it does not validate any operating system, device, meeting client, or capture mode.

## Environment records

Each run uses a named environment record. Fill in actual values; do not substitute a build agent label for an OS build number.

| Environment | Required role | Actual configuration | Status |
|---|---|---|---|
| ENV-HOME | Supported Windows 11 x64 Home, ordinary user | To be recorded | not-run |
| ENV-PRO | Supported Windows 11 x64 Pro, ordinary user | To be recorded | not-run |
| ENV-CLEAN | Clean Windows 11 x64 without development tools | To be recorded | not-run |
| ENV-MULTI | Two monitors with different DPI/scaling; equal-resolution pair also tested | To be recorded | not-run |
| ENV-AUDIO | Headset, USB audio, Bluetooth audio, and speakerphone configurations | To be recorded | not-run |

Environments can share a machine when the recorded configurations satisfy the cases. Capture full OS edition/build/patch, CPU/RAM, GPU/driver, monitor identifiers/resolutions/scaling/HDR, audio endpoint/driver, app revision/package hash, meeting client/browser version, and network type. Test the Windows versions actually declared supported at release time. Home must be tested rather than excluded by assumption.

## Capture-exclusion cases

Observe the shared output on a separate receiving client or a recording of the receiving stream. Looking at the local monitor or checking the API return value alone is insufficient. Use synthetic meeting content and avoid personal data in retained evidence.

For every row, exercise these Sidecue surfaces: main overlay, command bar, dashboard when applicable, settings, tooltips, context menus, secondary dialogs, and error surfaces. Record any surface that becomes visible, becomes a black rectangle, disappears correctly, or prevents capture. Verify the emergency hide shortcut and accurate unsupported/degraded status.

| Case | Meeting path | Sharing mode | Required variants | Status |
|---|---|---|---|---|
| CAP-01 | Microsoft Teams desktop | Application window | Home and Pro; overlay over the shared target; focus and secondary windows | not-run |
| CAP-02 | Microsoft Teams desktop | Entire monitor | Home and Pro; all Sidecue surfaces; mixed DPI; move overlay between monitors | not-run |
| CAP-03 | Zoom desktop | Application window | Home and Pro; overlay over the shared target; focus and secondary windows | not-run |
| CAP-04 | Zoom desktop | Entire monitor | Home and Pro; all surfaces; mixed DPI; fullscreen presentation target | not-run |
| CAP-05 | Google Meet in Chrome | Application window | Home and Pro; same browser version recorded; all relevant surfaces | not-run |
| CAP-06 | Google Meet in Chrome | Entire monitor | Home and Pro; all surfaces; monitor movement and sharing restart | not-run |
| CAP-07 | Google Meet in Edge | Application window | Home and Pro; same browser version recorded; all relevant surfaces | not-run |
| CAP-08 | Google Meet in Edge | Entire monitor | Home and Pro; all surfaces; monitor movement and sharing restart | not-run |
| CAP-09 | Supported browser meeting | Browser tab | Record the actual capture behavior and audio mode; do not infer results from window share | not-run |
| CAP-10 | Direct local capture API probe | Window and monitor | API return/error, DWM/window style, each owned top-level HWND, recreate after UI changes | not-run |

[SC-002A evidence](evidence/SC-002A/2026-11-06.md) covers the prototype's local HWND/API and lifecycle subset only. It includes no window/monitor capture observation and does not complete CAP-10 or any receiving-client row.

Each required variant needs its own evidence record. A row passes only when its required variants pass for the declared support set. Any untested capture path remains untested. A software/client/driver update that changes capture behavior requires rerunning affected rows. The public behavior is exclusion from verified supported capture paths, not universal undetectability or removal from Task Manager.

## Audio and session cases

| Case | Procedure | Expected observable result | Status |
|---|---|---|---|
| AUD-01 | Headset mic plus endpoint loopback for a two-party synthetic call | Distinct source labels, aligned timestamps, no unexplained duplication or missing interval | not-run |
| AUD-02 | Two remote speakers overlap and one repeats an earlier sentence | Remote speaker IDs remain anonymous unless assigned; genuine repetition is retained | not-run |
| AUD-03 | Speakerphone playback leaks into microphone | Echo/duplicate behavior is measured and attributed honestly; no false named-speaker certainty | not-run |
| AUD-04 | Unplug and reconnect USB headset during listening | Stream invalidation is visible, replacement has a new epoch, and missing interval is recorded | not-run |
| AUD-05 | Switch default playback and microphone devices | Device choice behavior matches settings; session persists; no silent fallback to an unintended source | not-run |
| AUD-06 | Switch Bluetooth profile or reconnect Bluetooth device | Format/endpoint changes recover or visibly fail; memory and queues remain bounded | not-run |
| AUD-07 | Pause listening while answer and document indexing are active | Audio acquisition/upload stops at a recorded cutoff; other activity is shown separately | not-run |
| AUD-08 | Disconnect network, restore it, then inject provider duplicate events | Bounded backlog, explicit gaps, new connection epoch, and no duplicate finalized segments | not-run |
| AUD-09 | Deny microphone permission or select unavailable endpoint | Actionable status and no fabricated transcript; retry does not loop indefinitely | not-run |
| AUD-10 | Capture selected process tree while other apps play audio | Included/excluded sources match configuration; browser process scope is labelled accurately | not-run |
| AUD-11 | Stop while the provider never finalizes its last partial | Bounded stop duration; unresolved tail is marked incomplete, with no invented final text | not-run |
| AUD-12 | Slow the consumer and force queue overflow | Latest audio continues with visible gap accounting; callbacks remain nonblocking | not-run |

## Desktop, screenshots, and lifecycle cases

| Case | Procedure | Expected observable result | Status |
|---|---|---|---|
| WIN-01 | Stream an answer while typing in the meeting client | Overlay does not steal focus; entering Sidecue text input is explicit | not-run |
| WIN-02 | Register a conflicting shortcut and hold a registered key | Conflict is explained; no unintended repeat flood; rebinding works | not-run |
| WIN-03 | Keyboard-only navigation and screen-reader pass | Core capture, Ask, pause, hide, end, settings, and evidence are operable and named | not-run |
| WIN-04 | Move overlay across 100/150/200 percent scaling and unplug a monitor | Readable bounds and placement survive; off-screen window recovers to a valid display | not-run |
| WIN-05 | Choose between equal-resolution monitors | Selected native identity controls capture; no dimension-based or first-monitor substitution | not-run |
| WIN-06 | Select a screenshot target, cancel, confirm, resize, and close target | Only the confirmed valid request sends pixels; preview is exact; resource use returns to baseline | not-run |
| WIN-07 | Capture HDR and SDR content, then simulate device loss | Preview conversion is usable; resource loss is handled visibly without stale frames | not-run |
| WIN-08 | Hide/show overlay, clear context, and start another session | Session/capture/epoch behavior matches each control; old answer events cannot leak across contexts | not-run |
| WIN-09 | Lock/unlock, sleep/wake, and end the user session | State/gaps are explicit; no orphan capture after application termination | not-run |
| WIN-10 | Fill disk or block database writes while speech continues | RecoveryRequired and unsaved-text indication appear before the bounded buffer is exhausted | not-run |
| WIN-11 | Kill app during capture and during a summary job | Only committed evidence recovers; jobs are idempotent; edited notes remain intact | not-run |

## Installation, updates, and duration cases

| Case | Procedure | Expected observable result | Status |
|---|---|---|---|
| PKG-01 | Install self-contained x64 package on ENV-CLEAN as an ordinary user | App and required native libraries start without developer tooling or admin operation | not-run |
| PKG-02 | Upgrade while a prior version has transcript and index data | User data outside RootAppDir is retained; migration is recoverable; update waits for safe session stop | not-run |
| PKG-03 | Attempt an invalid/tampered update and an unsupported older schema rollback | Update authenticity failure is visible; data is not silently corrupted or downgraded | not-run |
| PKG-04 | Uninstall and reinstall, then exercise explicit delete-data action separately | Ordinary uninstall preserves user data; explicit deletion obeys documented retention scope | not-run |
| PKG-05 | Restore encrypted backup within original usable profile and outside it | Same-profile restore works; unsupported profile migration fails clearly without claiming portable recovery | not-run |
| SOAK-01 | Two-hour meeting with transcript, periodic Ask, and selected evidence | Bounded transient resources; no silent dropped speech or stale answers; metrics captured | not-run |
| SOAK-02 | Overnight replay/capture with injected network/device events | Resource trend, recovery, and durable records remain consistent; failures are retained in report | not-run |

Use [the evidence template](../templates/TEST_EVIDENCE.md) for each run. Keep recordings/screenshots containing only synthetic test material in an explicit evidence location, not in application telemetry or ordinary CI logs.

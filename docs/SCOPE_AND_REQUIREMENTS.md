# Scope and requirements map

Sidecue is an original Windows desktop AI meeting assistant. It captures local microphone and meeting playback, transcribes live speech, offers concise contextual help, and creates editable meeting records. Initial AI processing uses configured cloud providers. The full supplied requirements are retained in [Requirements_Baseline.txt](../source/Requirements_Baseline.txt).

The current implementation scope is the personal Windows workflow through phase 4. Phase 5 covers integrations and managed/team features. Non-Windows and mobile work are deferred. The source snapshots predate this execution package and contain historical planning statements; the phase/status files here are authoritative for current work.

## Requirement coverage

| Source section | Requirement family | Implementation disposition |
| --- | --- | --- |
| 1 | No-bot meeting assistance and retrieval | Core personal product |
| 2 | Desktop, overlay, web, mobile | Windows desktop now; hosted portal later; mobile deferred |
| 3 | Audio, diagnostics, screen, calendar | Audio/diagnostics first; opt-in screenshot phase 3; calendar phase 5 |
| 4 | Transcription, retrieval, generation pipeline | Incremental phases 1 to 3 |
| 5 | Ask, actions, chips, coding mode, context control | Manual assistance first; automatic suggestions retained in EP-508 after G2 |
| 6 | Capture exclusion and visibility | Supported/tested OS exclusion; honest fallback status |
| 7 | Session lifecycle | Explicit states and recovery; calendar automation later |
| 8 | Notes, transcripts, export, sharing, follow-up | Personal artifacts/drafts phase 3; public sharing with hosted service |
| 9 | Pre-meeting briefs | Phase 5 using calendar/history/evidence |
| 10 | Knowledge import, retrieval, permissions | Personal corpus phase 3; shared sources/permissions later |
| 11 | CRM, ATS, calendar integrations | Phase 5 adapters with durable outbox |
| 12 | Coaching, scoring, ROI | Deferred; metrics need definitions and evaluation |
| 13 | Organizations, roles, SSO/SCIM, MDM, reports | Phase 5; do not build placeholder authorization |
| 14 | Profiles and customization | Personal versioned profiles phase 2; team approval/assignment later |
| 15 | Controls, settings, shortcuts, theme | Core controls early; accessibility/polish phase 4 |
| 16 | Languages | Separate input/output settings; validate claimed languages before release |
| 17 | Security, privacy, compliance | Data boundaries from phase 0; certifications need real separate evidence |
| 18 | Billing, tiers, entitlements | Phase 5; no initial paywall required |
| 19 | Updates, onboarding, support, referrals | Installation/update groundwork early; personal onboarding phase 4; business integrations later |
| 20 | Explicit limitations | Preserve no meeting bot, raw meeting recording, phone handling, autonomous desktop execution |

## Corrections and deliberate refinements

- DirectX is not the mechanism that guarantees invisibility. Use the documented window-affinity API and verify each supported sharing scenario.
- Do not categorically exclude Windows Home based on another vendor's behavior. Establish a tested Home/Pro matrix on supported releases.
- Encryption in transit and at rest does not mean end-to-end encryption against a cloud processor. Provider training/retention behavior depends on configured service terms and controls.
- Preserve the full transcript, but send bounded context. Advertised 400K or 1M token capacities are not requirements to fill those windows.
- Show operational progress and concise answers, not raw hidden model reasoning.
- Recover notes from committed interrupted sessions. A missing normal stop event is not grounds to discard available history.
- A calendar end time should prompt/check, not abruptly truncate an overrun meeting.
- Pausing listening, hiding, clearing context, and ending are distinct operations, with precise contract semantics.
- Ordinary uninstall preserves user data. Deleting it is a separate explicit operation.
- Portable recovery after Windows-profile loss is not supplied by current-user DPAPI backups. Do not claim it until implemented and tested.

## Phase scope discipline

Keep deferred capabilities in the backlog. Do not delete requirements because they are not in the first release, and do not prebuild billing/team infrastructure to appear comprehensive. If a requirement needs a different architecture, record the evidence and affected tasks before changing the baseline.

The definition of done is behavior plus appropriate evidence, not file count, a working mock screen, or an agent's confidence. The original product description is a feature source; it is not proof that Sidecue has any of those capabilities today.

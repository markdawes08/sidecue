# Evidence records

Store task evidence in a directory named after its task ID, with a descriptive filename and revision context. Copy [TEST_EVIDENCE.md](../../templates/TEST_EVIDENCE.md) and link the completed record from the backlog item.

[SC-000](SC-000/2026-11-06.md) records the initial solution build, architecture and shell tests, clean-source verification and limitations. Earlier [repository preparation](repository-preparation/2026-11-06.md) covers package and documentation checks only. Setup-only evidence may use a descriptive directory instead of a task ID.

Use synthetic fixtures and redacted metadata. Do not commit credentials, raw real meeting audio, screenshots of private content, complete provider payloads, or sensitive transcripts. Native sharing tests should use a synthetic demonstration screen and record the observed remote result with its exact environment.

An evidence record must identify what ran, where it ran, which revision/configuration it covered, its result, and any limitations. Keep failed and incomplete observations accurate; do not erase them to make a gate look complete.

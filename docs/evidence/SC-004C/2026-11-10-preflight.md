# SC-004C local durability preflight

## Identity

| Field | Value |
| --- | --- |
| Evidence ID | SC-004C-2026-11-10-local |
| Task and coverage | SC-004C, partial local interruption, migration and FTS verification; parent SC-004 and all gates remain open |
| Date and time | 2026-11-10T15:10:14-05:00 |
| Tester | Codex with delegated probe and independent security reviews |
| Result | partial: local cases pass; clean Windows and deployment evidence blocked or not run |
| Code | Uncommitted changes based on `dd0fe5e230c96c5205ad5acebf2f4bf9a6754f33`; tested source hashes below |
| Native DLL SHA-256 | `87d07385d9b299d0a2f65ec69e7d5636bf5b8b4b873bb5b2b5a65ba2848e2a92` |
| Managed binary SHA-256 | Infrastructure `c82659c2a87e3aff90f812967687aedf53670e95cbe0911cb7456d8b8e9af6a7`; DurabilityProbe `abaf5db69e83aed75c16bec496305d5e693d2e1b15daec67413e5857839851d5` |
| Architecture/contract | Baseline 0.1; no production schema or contract changed |

## Environment and configuration

Windows 11 Home x64, build 26200.9711, current developer profile, PowerShell 5.1, .NET SDK 10.0.403/runtime 10.0.14. The SQLCipher candidate is the reviewed binary from [SC-004A](../SC-004A/candidate-manifest.json), with SQLCipher 4.20.0 Community, SQLite 3.54.1 and OpenSSL 3.5.9. The production loader rechecks those identities and options before opening each test store. Test rows, keys and migration data were synthetic and local under ignored `artifacts/durability-probe/`.

VMware Workstation's `vmrun list` reported zero running VMs. Its two registered VM configurations were Linux guests, not a disposable clean Windows profile. Windows Sandbox was not found at the standard system path, and the machine is Windows 11 Home. These observations do not prove no Windows VM exists anywhere; no suitable clean Windows environment was established. No existing VM was started or changed. Hardware, audio, display, sharing and cloud providers were not relevant to these cases.

## Procedure

From the repository root, PowerShell:

```text
powershell -NoProfile -File tools/probe-durability.ps1
powershell -NoProfile -File tools/build.ps1
powershell -NoProfile -File tools/probe-durability.ps1 -NoBuild
powershell -NoProfile -File tools/build-sqlcipher.ps1 -PerlExecutable (Resolve-Path artifacts/sqlcipher-native/perl-runtime/ucrt64/bin/perl.exe).Path
```

The first probe, locked Release build and final probe exited 0; the build reported zero warnings and errors. The final command exited 1 as expected, with the explicit CVE guard message before any download, extraction, compilation or replacement. The reviewed native DLL's SHA-256 remained unchanged afterward. The full existing test suite was not repeated because this block changes no production application code or dependencies; the targeted native probe and solution build exercise the changed behavior.

The probe creates a separate encrypted store for each case. A child process opens it, performs a synthetic write or migration, and signals `READY` immediately before or after commit. The parent verifies the signal and terminates that child, then reopens the store under the same user. This tests abrupt process termination at controlled boundaries, not power failure, disk-controller behavior or every possible instruction inside a commit.

## Results

| Case | Observed | Result |
| --- | --- | --- |
| Write before commit | Baseline row and FTS remained; pending rows and index entries were absent after termination. | pass |
| Write after commit | Baseline plus 128 inserted rows and FTS entries remained after termination. | pass |
| Migration before commit | Original schema, version, data and FTS remained; the new column and index were absent. | pass |
| Migration after commit | New schema, version, index and transformed data remained alongside baseline FTS. | pass |
| Integrity and encryption | Each reopen had `cipher_status=1`, WAL mode, SQLite `integrity_check=ok`, and no SQLCipher integrity errors. Live database/WAL and final storage artifacts were scanned for UTF-8/UTF-16 synthetic markers. After-commit cases had a nonempty live WAL. | pass for the tested markers and files |
| Native rebuild guard | Pinned vulnerable Perl could not invoke the checked-in rebuild recipe; the original candidate hash was unchanged. | pass for the tested invocation |
| Clean Windows and exact distribution | No disposable clean Windows environment or package was available. | blocked / not-run |
| Actual installed directory and startup provider ownership | The application does not yet package the DLL or wire storage into startup. | not-run |

Marker scans do not establish absence of every plaintext representation, OS paging or crash dumps. The test migration is deliberately synthetic and does not validate production schema migrations or backup behavior.

## Security and remaining work

The [CPAN Security Group advisory](https://lists.security.metacpan.org/cve-announce/msg/42536248/) identifies versions through Perl 5.45.1 as affected by CVE-2026-15534. The [MSYS2 package record](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-perl) lists the CVE for the pinned build-only 5.44.0-3 package. The recipe's version check cannot establish whether the two upstream patches were applied. No patched native Windows toolchain was established in this run, so the rebuild recipe was disabled. The existing DLL can still be tested by its reviewed hash; the verified source inputs reduce exposure but do not prove the vulnerable code path was unreachable. Perl itself is not shipped in that DLL.

The loader depends on containing directories being protected from untrusted modifications and on exclusive SQLitePCL provider initialization. It checks identity and rejects reparse points but cannot close a concurrent pathname substitution race by itself; a substituted DLL's initialization could execute before a post-load check. The test directory's current ACL does not prove the future installed directory is protected. SC-006A/B retain the actual package notices, installed-directory and startup ownership checks. No application packaging or production wiring was added here.

Follow [the clean-environment procedure](clean-environment.md) once a disposable clean Windows environment is available. The authoritative backlog retains the required clean-run, toolchain and distribution actions. SC-004C cannot be marked done and the parent cannot close on this preflight alone.

## Tested source hashes

| File | SHA-256 |
| --- | --- |
| `tests/Sidecue.DurabilityProbe/Program.cs` | `28a79bfa702bda5a17575f5aa4d2d0f8b0dd1381e261bb62560624bcd2ca8483` |
| `tests/Sidecue.DurabilityProbe/Sidecue.DurabilityProbe.csproj` | `cb47bd3af01e06fa482d4a9ff40d683638a9608f5b3f8cac6b5fb2e9bec70ef3` |
| `tests/Sidecue.DurabilityProbe/packages.lock.json` | `d3fa7ea16e9b03de0bdbecdc09fd13493c3d6fd2209c581d2bfd04328b71af48` |
| `tools/probe-durability.ps1` | `82a1acf66257cba0788576b18b8899e29db46cb0484a72ddd20ea69357603a86` |
| `tools/build-sqlcipher.ps1` | `49ba0e8468b74913f9d041dfd581d84a32cd3a1e0c1f2667fd3bc4df106c6098` |

No tested probe or rebuild-guard code changed after the final run. Synthetic databases, protected keys, temporary output and build binaries remain ignored; only code, lockfiles and sanitized evidence are intended for Git.

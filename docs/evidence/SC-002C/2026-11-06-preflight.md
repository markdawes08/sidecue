# SC-002C local verification: partial run

## Identity and environment

| Field | Value |
| --- | --- |
| Evidence ID / task | SC-002C-2026-11-06-preflight / SC-002C; partial WIN-01, WIN-02 and WIN-04 preparation |
| Result | blocked for completion: the required second monitor and mixed-DPI setup are unavailable |
| Parent coverage | SC-002 scope 2/3, acceptance 2/3 and validation 2 remain open; this is not a parent or matrix-row pass |
| Time | November 6, 2026, 20:30 to 20:36 UTC-05:00 |
| Tester | Codex coordinator with delegated read-only environment and task reviews |
| Code revision | `0da3d81337093ab9719e2bf7b17dcd9f84d96452`, clean at start; no product code changed |
| Tested Desktop DLL SHA-256 | `9e39ca7ad95bebd20e067a6dc814f092af13a4e055ee843b51bda850afb4074a` |
| Tested Desktop EXE SHA-256 | `bb0eed4d05da08cac164cc947e6135d602f5c47b49ce3284bed16691218ba805` |
| Baseline | Architecture 0.1; Windows 11 x64 prototype, existing Release build |

Windows 11 Home 25H2 build 26200.9711, console session, 64-bit; workspace SDK 10.0.403 and .NET/WindowsDesktop runtime 10.0.14. NVIDIA GeForce RTX 4070 Laptop GPU driver 32.0.16.1656 and Intel UHD Graphics driver 32.0.101.6790. Enumeration found **one** active primary display, 2560 x 1600 pixels at 144 DPI (150%). Three Windows keyboard entries reported OK, and a foreground window was present. No meeting client, receiving client, provider, audio input or private meeting data was used. No screenshot or typed draft was saved in this evidence.

The owner confirmed that a second monitor cannot be connected now. Monitor movement across differing scales, unplug/recovery and the required two-monitor acceptance cannot be assessed in this environment. User-interactive status and keyboard enumeration do not substitute for those checks.

## Procedure

Working directory: repository root. PowerShell 5.1, ordinary interactive console. The coordinator and environment reviewer ran the read-only inspection, including DPI-aware `EnumDisplayMonitors`, `GetMonitorInfo` and `GetDpiForMonitor`:

```powershell
powershell -NoProfile -File docs/evidence/SC-002C/inspect-displays.ps1
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object DisplayVersion,CurrentBuild,UBR
Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion
Get-FileHash -Algorithm SHA256 src/Sidecue.Desktop/bin/Release/net10.0-windows10.0.26100.0/Sidecue.Desktop.dll,src/Sidecue.Desktop/bin/Release/net10.0-windows10.0.26100.0/Sidecue.Desktop.exe
```

Inspection exited 0. The complete sanitized output is [display-inspection.txt](display-inspection.txt); the script is [inspect-displays.ps1](inspect-displays.ps1). A second enumeration at 20:36 still found one display. These observations describe hardware availability, not overlay acceptance.

The ordinary direct app launch initially could not find the workspace-only .NET runtime. Launching from the build shell inherited high integrity; the Windows computer-use helper could inspect but not control it. That test-owned process was closed. For the limited UI run, a local as-invoker launcher was compiled and launched through the ordinary user session. It sets `DOTNET_ROOT` and `DOTNET_ROOT_X64` only in the child process; it changes no persistent settings. Source is [LaunchSidecue.cs](LaunchSidecue.cs), with [as-invoker.manifest](as-invoker.manifest). The local launcher EXE remains ignored under `artifacts/sc-002c-local/` and had SHA-256 `6d80222c6b2215f91eab139b46a9a3cc8f58017cdf9c8575ecaecb5a9732643e`.

To rebuild it on this machine, after a Release build and local SDK setup:

```powershell
New-Item -ItemType Directory -Force artifacts/sc-002c-local | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /reference:System.Windows.Forms.dll /win32manifest:docs/evidence/SC-002C/as-invoker.manifest /out:artifacts/sc-002c-local/LaunchSidecue.exe docs/evidence/SC-002C/LaunchSidecue.cs
```

The Windows computer-use helper launched that EXE at ordinary integrity. Its launch call reported that the short-lived launcher itself had no targetable window, but a fresh Sidecue window was subsequently found and inspected. Actions below were injected by the helper, not physical keyboard presses. The test-owned instances were closed afterward.

## Ordered observations

1. The first instance displayed the fixed-shortcut status in its shell. An injected Ctrl+Alt+Space was followed by an accessibility tree containing the display overlay, display-mode status and disabled local textbox.
2. Injected Ctrl+Alt+Enter was followed by input-mode status, an enabled local textbox and that textbox identified as focused. The helper sent the synthetic draft `Synthetic input 123`, but its accessibility output did not expose the textbox value. Therefore actual character entry was **not verified**.
3. Injected Ctrl+Alt+Escape was followed by an accessibility tree without the overlay. This checks one injected show/input/hide sequence, not key holding, all hide controls or physical keyboard operation.
4. A second instance was launched while the first held the registrations. Its shell visibly displayed error 1409 for all three shortcuts and directed the user to the buttons. Selecting **Window details** exposed separate overlay and details windows in the accessibility tree. Each displayed the Windows API acceptance and unverified-sharing notice. API acceptance is not observer-side proof.
5. An attempt to click the second shell's Hide button while both owned windows were open had no observed effect. The captured shell view omitted the excluded overlay surfaces, so the actual hit target could not be established. Treat that attempted click as **inconclusive**, not as proof of a product failure or a working hide path. Both test-owned processes were closed through their own main windows.

The prototype has no shell control that drives `OverlayHost.UpdateDisplay`. The real-foreground streaming-update case needs a narrow controlled test entry point or harness and a separate synthetic foreground app. The earlier SC-002B probe used an in-process owner and directed messages; it is not a substitute. The supported prototype has only overlay and details windows. Context menus, tooltips, native system menus, IME and spellcheck popups are suppressed; no stock dialog or popup was exercised in this run.

## Results and limitations

| Case / variant | Observed | Result |
| --- | --- | --- |
| Active displays and DPI | One 2560 x 1600 primary display, 150%; user cannot add a second now | blocked for mixed-DPI prerequisite |
| Injected shortcut sequence | Display opened, explicit input mode became enabled/focused, hide removed the overlay from the accessibility tree | pass for this limited injected sequence only |
| Synthetic text entry | Input sent; resulting textbox value unavailable to the helper | not-run as a verified typing outcome |
| Shortcut conflict | Second instance visibly reported error 1409 for all three fixed bindings | pass for this local conflict case only |
| Details policy status | Separate details window reported API acceptance and unverified-sharing limitation | pass for visible local status only |
| Shell Hide with both surfaces present | Input target could not be established; no effect observed | not-run as a verified outcome |
| Real foreground app during display updates | No separate synthetic foreground app or controlled update stream exercised | not-run |
| Physical keyboard/holding a key, placement and popup interaction | Not exercised | not-run |
| Two monitors, mixed DPI and unplug/recovery | Second display unavailable | blocked |
| Remote screen sharing | Outside this leaf; no receiving client | not-run |

No full WIN-01, WIN-02, WIN-04 or CAP row passes. WIN-02 also includes rebinding, which this fixed-shortcut prototype does not implement. No phase gate passes. Prior [SC-002B evidence](../SC-002B/2026-11-06.md) remains separate from this partial run.

## Artifacts and handoff

| Artifact | SHA-256 |
| --- | --- |
| [Display inspection output](display-inspection.txt) | `c8eee1eeecce009af05a242410b77c0d99e5e5374008bc997525d0f21cf1655d` |
| [Display inspection source](inspect-displays.ps1) | `ad219473059e4ba04f075702d0ab5d9c7dc021ddd236be906581256c58643bb1` |
| [Launcher source](LaunchSidecue.cs) | `6083ba7eb3607fdae553c15b80eee847d6e1cb8e34528820ea4c8bd9e69b73a0` |
| [Launcher manifest](as-invoker.manifest) | `10e51df18426022a7994105ac6b3ca997efe0d2dcac2070ebadd0d63718beed8` |

The display script and local launcher ran as described. No product build or automated product suite was rerun because product code and dependencies did not change. Repository checks exited 0:

```powershell
powershell -NoProfile -File tools/check-repository.ps1
powershell -NoProfile -File tools/show-task.ps1 -Id SC-002C
git diff --check
git diff --quiet -- README.md
```

The repository checker validated 19 JSON files, 134 tasks, five gates, dependency/coverage/completion guards, 255 local links and both original source hashes. The task viewer reports SC-002C blocked. Existing ignore rules keep compiled and generated test output under `artifacts/` while retaining these sanitized evidence files. Nothing was staged, committed or pushed; the README was not consulted or edited.

SC-002C remains blocked and unowned until a second active display at a different scaling is available. On resumption, check the display setup, clear the blocker, claim the leaf and finish its required foreground, text-input, keyboard, auxiliary and mixed-DPI cases. Preserve this partial record; do not convert its limited observations to full acceptance. Parent SC-002 and all gates remain unchanged.

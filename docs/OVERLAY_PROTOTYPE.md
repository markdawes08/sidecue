# Overlay prototype

SC-002A adds an owned WPF overlay with capture-exclusion capability reporting. Run `powershell -NoProfile -File tools/run.ps1`, then choose **Show overlay prototype** in the shell. Hide it with its button, Escape while focused, or the shell's Hide button. Meeting features are still unavailable.

The overlay reports the Windows API outcome and always states that screen-sharing behavior has not been verified. A failed or unavailable request leaves hiding available. Non-activating display, deliberate input modes, global shortcuts and auxiliary-window policy belong to SC-002B; remote-observer checks belong to later leaves.

## Implementation boundary

[WindowCaptureExclusion](../src/Sidecue.Windows/Windowing/WindowCaptureExclusion.cs) requests exactly `WDA_EXCLUDEFROMCAPTURE` (`0x11`) through an injectable native API. It distinguishes unsupported Windows, disabled composition, composition-query failure and native request failure. Win32 errors and HRESULTs remain separate; it never silently substitutes another affinity mode.

The native function requires a top-level window in the calling process and depends on desktop composition. Its success does not guarantee exclusion from every capture mechanism. [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) defines these limitations.

[OverlayWindow](../src/Sidecue.Desktop/Prototypes/Overlay/OverlayWindow.xaml.cs) applies the policy in `OnSourceInitialized`, once the HWND is ready. [OverlayHost](../src/Sidecue.Desktop/Prototypes/Overlay/OverlayHost.cs) reuses a hidden window, creates a fresh instance after Close, and closes it with its owner. Policy follows each window lifetime, even if Windows recycles a numerical handle. The overlay is opaque, owned and topmost. It uses ordinary activating behavior until the next block adds explicit focus modes.

The desktop executable declares Windows 10/11 compatibility in [app.manifest](../src/Sidecue.Desktop/app.manifest). This avoids the legacy compatibility response documented for [DwmIsCompositionEnabled](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmiscompositionenabled). The project's supported platform remains Windows 11 x64; the shared compatibility GUID does not broaden that support claim. See [Microsoft's manifest reference](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests).

## Local probe

```powershell
powershell -NoProfile -File tools/probe-overlay.ps1
# Reuse an unchanged build:
powershell -NoProfile -File tools/probe-overlay.ps1 -NoBuild
```

This separate command needs a local interactive Windows desktop. It launches the actual manifested executable in diagnostic mode, briefly shows synthetic owner/overlay windows, then checks hide/show, close/recreation, affinity reapplication, the Hide control and owner cleanup. It closes its windows and returns a nonzero exit on lifecycle/reporting failure. An unavailable affinity remains an explicit capability result in the report, rather than being reported as exclusion success.

Outputs are under ignored `artifacts/overlay-probe/`: `native-probe.json` contains API and lifecycle observations; `overlay-preview.png` renders only the synthetic WPF content. The preview is not a captured screen or receiving-stream recording. [GetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowdisplayaffinity) readback is diagnostic only and has its own composition/window-style requirements.

The ordinary suite uses six native-boundary cases and six WPF lifecycle cases with injected outcomes. These validate failure messaging and local controls; they cannot prove remote sharing behavior. See [SC-002A evidence](evidence/SC-002A/2026-11-06.md) for the actual local probe and its limits. No CAP matrix row or phase gate is completed by API acceptance alone.

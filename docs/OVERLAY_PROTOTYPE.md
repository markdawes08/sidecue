# Overlay prototype

Run `powershell -NoProfile -File tools/run.ps1`. **Show display** opens the prototype without requesting focus. **Enter text mode** deliberately requests focus and enables the local test textbox. **Window details** opens a second owned surface with its own capture-exclusion result. Drag either heading to move its window. Meeting features are still unavailable; test text is neither sent nor saved.

The shell reports whether each fixed shortcut registered:

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+Space | Show the display, or hide all overlay windows if already visible |
| Ctrl+Alt+Enter | Enter text mode |
| Ctrl+Alt+Escape | Hide all overlay windows |

If another app owns a shortcut, the shell shows its Windows error and the buttons remain available. There is no automatic rebinding or retry loop. Escape while typing and either window's Hide button also hide both surfaces. Hiding does not clear the local draft or display text.

The overlay always distinguishes Windows API acceptance from verified screen sharing. A failed exclusion request stays visible with an immediate hide path. A failed native focus-mode change hides the surfaces, disables input and reports the error in the shell. If Windows declines an explicit foreground request, text mode says to select the input box. No focus or capture guarantee is inferred from a successful API call.

## Implementation boundary

[WindowCaptureExclusion](../src/Sidecue.Windows/Windowing/WindowCaptureExclusion.cs) requests exactly `WDA_EXCLUDEFROMCAPTURE` (`0x11`) through an injectable native API. It distinguishes unsupported Windows, disabled composition, composition-query failure and native request failure. Win32 errors and HRESULTs remain separate; it never silently substitutes another affinity mode.

The native function requires a top-level window in the calling process and depends on desktop composition. Its success does not guarantee exclusion from every capture mechanism. [Microsoft's API contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity) defines these limitations.

[OverlaySurfacePolicy](../src/Sidecue.Desktop/Prototypes/Overlay/OverlaySurfacePolicy.cs) applies exclusion when each HWND is ready. [OverlayHost](../src/Sidecue.Desktop/Prototypes/Overlay/OverlayHost.cs) retains hidden windows, creates fresh instances after Close and releases both surfaces with their owner. Moving or changing bounds does not recreate the window. Each new HWND lifetime reapplies exclusion even if Windows recycles its numerical handle.

`OverlayHost.Show()` selects display mode. `ShowInteractive()` is the sole deliberate activation path. `UpdateDisplay(string)` updates content without showing a hidden window, changing mode, applying native styles or requesting focus. It retains the latest text even before the first show. These are prototype entry points, not a provider/session integration.

[WindowActivationPolicy](../src/Sidecue.Windows/Windowing/WindowActivationPolicy.cs) toggles `WS_EX_NOACTIVATE` and refreshes styles with `SWP_NOACTIVATE`. The windows also use `ShowActivated=false` and reject mouse activation in display mode. WPF's [ShowActivated contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showactivated?view=windowsdesktop-10.0) alone only covers opening, so it is not the entire policy. See Microsoft's [extended styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) and [SetWindowPos contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).

[OverlayKeyboardController](../src/Sidecue.Desktop/Prototypes/Overlay/OverlayKeyboardController.cs) routes owner-window messages only for successful registrations. [OverlayHotkeyRegistration](../src/Sidecue.Windows/Windowing/OverlayHotkeyRegistration.cs) permits one attempt at three fixed bindings, uses `MOD_NOREPEAT`, and records each unregister result. Owner destruction removes the message hook and releases registrations during `WM_DESTROY`, while the HWND is still valid; WPF `Closed` alone is too late. The [RegisterHotKey contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) defines conflicts and repeat suppression. Shortcut customization remains deferred.

Both surfaces are opaque, topmost and borderless. The prototype suppresses native system menus and WPF context menus/tooltips, including ScrollBar template menus. The test textbox disables IME and spellcheck popups. These restrictions keep implicit secondary HWNDs outside the prototype's supported surface set. The details window is the only supported auxiliary surface. Any future popup, dialog, tooltip or IME support must first join an explicit exclusion policy and receive its own checks; do not add a stock Popup or ShowDialog and assume it inherits its owner's affinity. Full input-method support and accessibility remain later product work.

This suppression matters because [ContextMenuService.IsEnabled](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.contextmenuservice.isenabled?view=windowsdesktop-10.0) does not inherit from the root window. WPF also creates a [default ScrollBar menu](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ScrollBar.cs) and retains the [native system-menu style](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs) on borderless windows. Shared guards and explicit ScrollBar styles cover these existing surfaces.

The desktop executable declares Windows 10/11 compatibility in [app.manifest](../src/Sidecue.Desktop/app.manifest). This avoids the legacy compatibility response documented for [DwmIsCompositionEnabled](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmiscompositionenabled). The project's supported platform remains Windows 11 x64; the shared compatibility GUID does not broaden that support claim. See [Microsoft's manifest reference](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests).

## Local probe

```powershell
powershell -NoProfile -File tools/probe-overlay.ps1
# Reuse an unchanged build:
powershell -NoProfile -File tools/probe-overlay.ps1 -NoBuild
```

This separate command needs a local interactive Windows desktop. It briefly opens synthetic owner/overlay/details windows and checks native mode styles, input enablement, update/placement identity, hide/show, recreation, affinity application and owner cleanup. It also registers the fixed shortcuts, attempts competing registrations, sends directed `WM_HOTKEY` notifications, then checks release and reacquisition. Those messages are not physical key presses. Existing shortcut conflicts are recorded rather than treated as an app failure.

The probe closes its windows and returns a nonzero exit on an assertion/reporting failure. Unavailable affinity and a denied activation request remain explicit capability results. They do not become capture or physical typing success claims.

Outputs are under ignored `artifacts/overlay-probe/`: `native-probe.json` contains API/lifecycle observations; `overlay-preview.png`, `input-preview.png` and `details-preview.png` render only synthetic WPF content. These are not captured screens or receiving-stream recordings. [GetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowdisplayaffinity) readback is diagnostic only and has its own composition/window-style requirements.

The ordinary suite covers injected native failures, successful and failed registration, bounded lifecycle, display/input transitions, hidden updates and auxiliary failure handling. See [SC-002B evidence](evidence/SC-002B/2026-11-06.md) and the earlier [SC-002A record](evidence/SC-002A/2026-11-06.md). SC-002C still requires physical input, a real foreground application and two monitors with mixed DPI. Receiving-client checks remain SC-002D through SC-002F. No full WIN/CAP row or phase gate is completed by these local checks.

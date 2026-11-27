using System.Windows;
using System.Windows.Interop;
using Sidecue.Windows.ScreenCapture;

namespace Sidecue.Desktop.Prototypes.ScreenCapture;

/// <summary>Runs the local prototype from target selection through one screenshot preview.</summary>
public sealed class ScreenshotCaptureFlow
{
    private readonly Func<nint, CancellationToken, Task<OwnedScreenshotFrame?>> _capture;
    private readonly Func<Window, OwnedScreenshotFrame, CancellationToken, Task<OwnedScreenshotFrame?>> _preview;

    public ScreenshotCaptureFlow() : this(WindowsScreenshotCapture.PickAndCaptureAsync,
        (owner, frame, cancellationToken) => ScreenshotPreviewWindow.ShowAsync(owner, frame, cancellationToken: cancellationToken))
    {
    }

    /// <summary>Accepts capture and preview delegates. The preview takes ownership of the supplied frame.</summary>
    public ScreenshotCaptureFlow(
        Func<nint, CancellationToken, Task<OwnedScreenshotFrame?>> capture,
        Func<Window, OwnedScreenshotFrame, CancellationToken, Task<OwnedScreenshotFrame?>> preview)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(preview);
        _capture = capture;
        _preview = preview;
    }

    /// <summary>Returns whether the preview was included, then releases the approved frame.</summary>
    public async Task<bool> PickAndPreviewAsync(Window owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();

        nint ownerWindow = new WindowInteropHelper(owner).Handle;
        using OwnedScreenshotFrame? frame = await _capture(ownerWindow, cancellationToken);
        if (frame is null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using OwnedScreenshotFrame? approved = await _preview(owner, frame, cancellationToken);
        return approved is not null && !cancellationToken.IsCancellationRequested;
    }
}

using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace Sidecue.Windows.ScreenCapture;

/// <summary>Captures one frame from the target selected in the Windows picker.</summary>
public static class WindowsScreenshotCapture
{
    /// <summary>Invoke on the owning window's dispatcher. The caller owns any returned frame.</summary>
    public static Task<OwnedScreenshotFrame?> PickAndCaptureAsync(nint ownerWindow,
        CancellationToken cancellationToken = default) =>
        CaptureSelectedAsync(ct => PickTargetAsync(ownerWindow, ct), WindowsGraphicsCaptureFrame.CaptureAsync,
            cancellationToken);

    internal static async Task<GraphicsCaptureItem?> PickTargetAsync(nint ownerWindow, CancellationToken cancellationToken)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("A ready window is required for the Windows picker.", nameof(ownerWindow));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.Unsupported);
        }

        try
        {
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, ownerWindow);
            return await picker.PickSingleItemAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed);
        }
    }

    internal static async Task<OwnedScreenshotFrame?> CaptureSelectedAsync<T>(
        Func<CancellationToken, Task<T?>> pick, Func<T, CancellationToken, Task<OwnedScreenshotFrame>> capture,
        CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        T? selected = await pick(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (selected is null)
        {
            return null;
        }

        OwnedScreenshotFrame frame = await capture(selected, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            frame.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return frame;
    }
}

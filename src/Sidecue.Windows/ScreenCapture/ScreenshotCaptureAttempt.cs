using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Sidecue.Windows.ScreenCapture;

internal enum ScreenshotColorMode { Sdr, AdvancedColor, Unknown }

internal interface IScreenshotCaptureRun : IDisposable
{
    // The close flag remains valid after native resources have been released.
    bool TargetClosed { get; }
    Task<OwnedScreenshotFrame> ReadFrameAsync(CancellationToken cancellationToken);
}

internal static class ScreenshotCaptureAttempt
{
    internal static async Task<OwnedScreenshotFrame> RunAsync(Func<IScreenshotCaptureRun> open,
        Func<ScreenshotColorMode> readColorMode, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        CancellationToken captureToken = deadline.Token;
        IScreenshotCaptureRun? run = null;
        OwnedScreenshotFrame? frame = null;
        bool transferred = false;
        try
        {
            captureToken.ThrowIfCancellationRequested();
            RequireSdr(readColorMode());
            captureToken.ThrowIfCancellationRequested();

            run = open();
            using (run)
            {
                CheckTarget(run, captureToken);
                frame = await run.ReadFrameAsync(captureToken).ConfigureAwait(false);
                CheckTarget(run, captureToken);
                RequireSdr(readColorMode());
            }

            CheckTarget(run, captureToken);
            transferred = true;
            return frame;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScreenshotCaptureException(run?.TargetClosed == true ? ScreenshotCaptureFailure.TargetClosed :
                captureToken.IsCancellationRequested ? ScreenshotCaptureFailure.TimedOut : ScreenshotCaptureFailure.CaptureFailed);
        }
        catch (Exception exception) when (exception is ExternalException or UnauthorizedAccessException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ScreenshotCaptureException(run?.TargetClosed == true
                ? ScreenshotCaptureFailure.TargetClosed : TranslateNativeFailure(exception.HResult));
        }
        finally
        {
            if (!transferred)
            {
                frame?.Dispose();
            }
        }
    }

    private static void CheckTarget(IScreenshotCaptureRun run, CancellationToken captureToken)
    {
        captureToken.ThrowIfCancellationRequested();
        if (run.TargetClosed)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.TargetClosed);
        }
    }

    private static void RequireSdr(ScreenshotColorMode mode)
    {
        if (mode != ScreenshotColorMode.Sdr)
        {
            throw new ScreenshotCaptureException(mode == ScreenshotColorMode.AdvancedColor
                ? ScreenshotCaptureFailure.AdvancedColor : ScreenshotCaptureFailure.DisplayColorUnknown);
        }
    }

    internal static void ValidateFrame(SizeInt32 size, SizeInt32 content, Direct3DSurfaceDescription description)
    {
        if (content.Width != size.Width || content.Height != size.Height ||
            description.Width != size.Width || description.Height != size.Height)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.SizeChanged);
        }
        if (description.Format != DirectXPixelFormat.B8G8R8A8UIntNormalized)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.UnsupportedFormat);
        }
    }

    private static ScreenshotCaptureFailure TranslateNativeFailure(int hresult) => hresult switch
    {
        // DXGI_ERROR_DEVICE_REMOVED / DEVICE_HUNG / DEVICE_RESET / DRIVER_INTERNAL_ERROR.
        unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or
            unchecked((int)0x887A0007) or unchecked((int)0x887A0020) => ScreenshotCaptureFailure.DeviceLost,
        unchecked((int)0x887A0004) => ScreenshotCaptureFailure.Unsupported,
        unchecked((int)0x887A0027) => ScreenshotCaptureFailure.TimedOut,
        _ => ScreenshotCaptureFailure.CaptureFailed
    };
}

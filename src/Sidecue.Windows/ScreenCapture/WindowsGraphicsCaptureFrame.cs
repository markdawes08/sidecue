using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Sidecue.Windows.ScreenCapture;

internal static class WindowsGraphicsCaptureFrame
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);

    internal static Task<OwnedScreenshotFrame> CaptureAsync(GraphicsCaptureItem item, CancellationToken cancellationToken) =>
        Task.Run(() => CaptureCoreAsync(item, cancellationToken), cancellationToken);

    private static async Task<OwnedScreenshotFrame> CaptureCoreAsync(
        GraphicsCaptureItem item, CancellationToken cancellationToken)
    {
        using var captureDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        captureDeadline.CancelAfter(CaptureTimeout);
        CancellationToken captureToken = captureDeadline.Token;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int targetClosed = 0;
        TypedEventHandler<GraphicsCaptureItem, object> onClosed = (_, _) =>
        {
            Interlocked.Exchange(ref targetClosed, 1);
            ready.TrySetResult();
        };
        OwnedScreenshotFrame? result = null;
        bool transferred = false;
        try
        {
            item.Closed += onClosed;
            try
            {
                SizeInt32 size = item.Size;
                int byteCount = ValidateSize(size);
                using IDirect3DDevice device = WindowsCaptureDevice.Create();
                using Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
                using GraphicsCaptureSession session = pool.CreateCaptureSession(item);
                TypedEventHandler<Direct3D11CaptureFramePool, object> onFrameArrived = (_, _) => ready.TrySetResult();
                pool.FrameArrived += onFrameArrived;
                try
                {
                    captureToken.ThrowIfCancellationRequested();
                    session.StartCapture();
                    await ready.Task.WaitAsync(captureToken).ConfigureAwait(false);
                    ThrowIfTargetClosed(Volatile.Read(ref targetClosed));
                    captureToken.ThrowIfCancellationRequested();

                    using Direct3D11CaptureFrame frame = pool.TryGetNextFrame() ??
                        throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed);
                    using IDirect3DSurface surface = frame.Surface;
                    VerifyFrameSizeAndFormat(size, frame.ContentSize, surface.Description);

                    using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Straight)
                        .AsTask(captureToken).ConfigureAwait(false);
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.PixelWidth != size.Width ||
                        bitmap.PixelHeight != size.Height)
                    {
                        throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed);
                    }

                    var owner = new PixelBuffer(byteCount);
                    result = new OwnedScreenshotFrame(size.Width, size.Height, owner);
                    bitmap.CopyToBuffer(owner.Bytes.AsBuffer());
                }
                finally
                {
                    pool.FrameArrived -= onFrameArrived;
                }
            }
            finally
            {
                item.Closed -= onClosed;
            }

            ThrowIfTargetClosed(Volatile.Read(ref targetClosed));
            captureToken.ThrowIfCancellationRequested();
            transferred = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.TimedOut);
        }
        catch (Exception exception) when (exception is ExternalException or UnauthorizedAccessException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTargetClosed(Volatile.Read(ref targetClosed));
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed);
        }
        finally
        {
            if (!transferred)
            {
                result?.Dispose();
            }
        }
    }

    private static void VerifyFrameSizeAndFormat(SizeInt32 size, SizeInt32 content, Direct3DSurfaceDescription description)
    {
        if (content.Width != size.Width || content.Height != size.Height ||
            description.Width != size.Width || description.Height != size.Height)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.SizeChanged);
        }
        if (description.Format != DirectXPixelFormat.B8G8R8A8UIntNormalized)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.Unsupported);
        }
    }

    private static int ValidateSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.TargetClosed);
        }

        try
        {
            return OwnedScreenshotFrame.GetByteCount(size.Width, size.Height);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.ImageTooLarge);
        }
    }

    private static void ThrowIfTargetClosed(int targetClosed)
    {
        if (targetClosed != 0)
        {
            throw new ScreenshotCaptureException(ScreenshotCaptureFailure.TargetClosed);
        }
    }

    private sealed class PixelBuffer(int byteCount) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[byteCount];

        internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(PixelBuffer));

        public Memory<byte> Memory => Bytes;

        public void Dispose() => _bytes = null;
    }
}

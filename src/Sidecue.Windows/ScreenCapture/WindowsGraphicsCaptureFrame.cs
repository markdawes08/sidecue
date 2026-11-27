using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
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
        Task.Run(() => ScreenshotCaptureAttempt.RunAsync(() => CaptureRun.Open(item), WindowsDisplayColor.GetActiveMode,
            CaptureTimeout, cancellationToken), cancellationToken);

    private sealed class CaptureRun(GraphicsCaptureItem item) : IScreenshotCaptureRun
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDirect3DDevice? _device;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private SizeInt32 _size;
        private int _byteCount;
        private int _targetClosed;
        private bool _observingTarget;
        private bool _observingFrames;
        private bool _disposed;

        public bool TargetClosed => Volatile.Read(ref _targetClosed) != 0;

        internal static CaptureRun Open(GraphicsCaptureItem item)
        {
            var run = new CaptureRun(item);
            try
            {
                run.Initialize();
                return run;
            }
            catch
            {
                try { run.Dispose(); }
                catch (Exception)
                {
                    // Preserve the acquisition error. Dispose attempts every release even if one fails.
                }
                throw;
            }
        }

        private void Initialize()
        {
            item.Closed += OnTargetClosed;
            _observingTarget = true;
            _size = item.Size;
            _byteCount = ValidateSize(_size);
            ThrowIfTargetClosed();

            _device = WindowsCaptureDevice.Create();
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _size);
            _session = _pool.CreateCaptureSession(item);
            _pool.FrameArrived += OnFrameArrived;
            _observingFrames = true;
        }

        public async Task<OwnedScreenshotFrame> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTargetClosed();
            _session!.StartCapture();
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTargetClosed();

            OwnedScreenshotFrame? result = null;
            try
            {
                using (Direct3D11CaptureFrame frame = _pool!.TryGetNextFrame() ??
                    throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed))
                using (IDirect3DSurface surface = frame.Surface)
                {
                    ScreenshotCaptureAttempt.ValidateFrame(_size, frame.ContentSize, surface.Description);
                    using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Straight)
                        .AsTask(cancellationToken).ConfigureAwait(false);
                    if (bitmap.PixelWidth != _size.Width || bitmap.PixelHeight != _size.Height)
                    {
                        throw new ScreenshotCaptureException(ScreenshotCaptureFailure.SizeChanged);
                    }
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        throw new ScreenshotCaptureException(ScreenshotCaptureFailure.UnsupportedFormat);
                    }

                    var owner = new PixelBuffer(_byteCount);
                    result = new OwnedScreenshotFrame(_size.Width, _size.Height, owner);
                    bitmap.CopyToBuffer(owner.Bytes.AsBuffer());
                }

                return result;
            }
            catch
            {
                result?.Dispose();
                throw;
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) => _ready.TrySetResult();

        private void OnTargetClosed(GraphicsCaptureItem sender, object args)
        {
            Interlocked.Exchange(ref _targetClosed, 1);
            _ready.TrySetResult();
        }

        private void ThrowIfTargetClosed()
        {
            if (TargetClosed)
            {
                throw new ScreenshotCaptureException(ScreenshotCaptureFailure.TargetClosed);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            GC.SuppressFinalize(this);

            try
            {
                using IDirect3DDevice? device = _device;
                using Direct3D11CaptureFramePool? pool = _pool;
                using GraphicsCaptureSession? session = _session;
                _device = null;
                _pool = null;
                _session = null;

                if (_observingFrames && pool is not null)
                {
                    pool.FrameArrived -= OnFrameArrived;
                }
            }
            finally
            {
                // Keep observing target closure until the capture resources have finished closing.
                if (_observingTarget)
                {
                    item.Closed -= OnTargetClosed;
                }
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
    }

    private sealed class PixelBuffer(int byteCount) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[byteCount];

        internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(PixelBuffer));

        public Memory<byte> Memory => Bytes;

        public void Dispose() => _bytes = null;
    }
}

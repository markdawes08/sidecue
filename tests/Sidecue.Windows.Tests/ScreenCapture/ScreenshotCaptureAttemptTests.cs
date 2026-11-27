using System.Buffers;
using System.Runtime.InteropServices;
using Sidecue.Windows.ScreenCapture;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Xunit;

namespace Sidecue.Windows.Tests.ScreenCapture;

public sealed class ScreenshotCaptureAttemptTests
{
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(31, DirectXPixelFormat.B8G8R8A8UIntNormalized, ScreenshotCaptureFailure.SizeChanged)]
    [InlineData(32, DirectXPixelFormat.R16G16B16A16Float, ScreenshotCaptureFailure.UnsupportedFormat)]
    public async Task InvalidFrameMetadataReleasesTheCaptureRun(int contentWidth, DirectXPixelFormat format,
        ScreenshotCaptureFailure expected)
    {
        var run = new CaptureRun(_ =>
        {
            var selectedSize = new SizeInt32 { Width = 32, Height = 32 };
            var contentSize = new SizeInt32 { Width = contentWidth, Height = 32 };
            var surface = new Direct3DSurfaceDescription { Width = 32, Height = 32, Format = format };
            ScreenshotCaptureAttempt.ValidateFrame(selectedSize, contentSize, surface);
            throw new InvalidOperationException("Invalid metadata unexpectedly reached pixel readback.");
        });

        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => RunAsync(run));

        Assert.Equal(expected, exception.Reason);
        Assert.Equal(1, run.DisposeCalls);
    }

    [Fact]
    public async Task DeviceLossReleasesTheCaptureRunAndReportsItsCategory()
    {
        var run = new CaptureRun(_ => Task.FromException<OwnedScreenshotFrame>(
            Marshal.GetExceptionForHR(DeviceRemoved, new nint(-1))!));

        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => RunAsync(run));

        Assert.Equal(ScreenshotCaptureFailure.DeviceLost, exception.Reason);
        Assert.Equal(1, run.DisposeCalls);
    }

    [Fact]
    public async Task TargetClosureDuringCleanupReleasesTheCapturedPixels()
    {
        var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var run = new CaptureRun(_ => Task.FromResult(frame));
        run.OnDispose = () => run.TargetClosed = true;

        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => RunAsync(run));

        Assert.Equal(ScreenshotCaptureFailure.TargetClosed, exception.Reason);
        AssertReleased(run, pixels, frame);
    }

    [Fact]
    public async Task CancellationBeforeHandoffReleasesTheCapturedPixels()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var run = new CaptureRun(_ => Task.FromResult(frame)) { AfterRead = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScreenshotCaptureAttempt.RunAsync(
            () => run, () => ScreenshotColorMode.Sdr, Timeout, cancellation.Token));

        AssertReleased(run, pixels, frame);
    }

    [Theory]
    [InlineData(true, ScreenshotCaptureFailure.AdvancedColor)]
    [InlineData(false, ScreenshotCaptureFailure.DisplayColorUnknown)]
    public async Task NonSdrDesktopPreventsTheCaptureRunFromOpening(bool advancedColor,
        ScreenshotCaptureFailure expected)
    {
        var mode = advancedColor ? ScreenshotColorMode.AdvancedColor : ScreenshotColorMode.Unknown;
        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => ScreenshotCaptureAttempt.RunAsync(
            () => throw new InvalidOperationException("Capture opened without a verified SDR desktop."),
            () => mode, Timeout, TestContext.Current.CancellationToken));

        Assert.Equal(expected, exception.Reason);
    }

    [Fact]
    public async Task ChangingToAdvancedColorDuringReadbackReleasesTheCapturedPixels()
    {
        var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var mode = ScreenshotColorMode.Sdr;
        var run = new CaptureRun(_ => Task.FromResult(frame)) { AfterRead = () => mode = ScreenshotColorMode.AdvancedColor };

        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => ScreenshotCaptureAttempt.RunAsync(
            () => run, () => mode, Timeout, TestContext.Current.CancellationToken));

        Assert.Equal(ScreenshotCaptureFailure.AdvancedColor, exception.Reason);
        AssertReleased(run, pixels, frame);
    }

    [Fact]
    public async Task CleanupFailureReleasesTheCapturedPixelsBeforeReportingDeviceLoss()
    {
        var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var run = new CaptureRun(_ => Task.FromResult(frame))
        {
            OnDispose = () => Marshal.ThrowExceptionForHR(DeviceRemoved, new nint(-1)),
        };

        var exception = await Assert.ThrowsAsync<ScreenshotCaptureException>(() => RunAsync(run));

        Assert.Equal(ScreenshotCaptureFailure.DeviceLost, exception.Reason);
        AssertReleased(run, pixels, frame);
    }

    [Fact]
    public async Task SuccessfulHandoffReleasesTheRunAndKeepsTheImageWithTheCaller()
    {
        var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var run = new CaptureRun(_ => Task.FromResult(frame));

        OwnedScreenshotFrame result = await RunAsync(run);

        Assert.Same(frame, result);
        Assert.Equal(1, run.DisposeCalls);
        Assert.Equal(0, pixels.DisposeCalls);
    }

    private static Task<OwnedScreenshotFrame> RunAsync(CaptureRun run) => ScreenshotCaptureAttempt.RunAsync(
        () => run, () => ScreenshotColorMode.Sdr, Timeout, TestContext.Current.CancellationToken);

    private static void AssertReleased(CaptureRun run, TestBuffer pixels, OwnedScreenshotFrame frame)
    {
        Assert.Equal(1, run.DisposeCalls);
        Assert.Equal(1, pixels.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    private sealed class CaptureRun(Func<CancellationToken, Task<OwnedScreenshotFrame>> read) : IScreenshotCaptureRun
    {
        public bool TargetClosed { get; set; }
        internal Action? AfterRead { get; set; }
        internal Action? OnDispose { get; set; }
        internal int DisposeCalls { get; private set; }

        public async Task<OwnedScreenshotFrame> ReadFrameAsync(CancellationToken cancellationToken)
        {
            OwnedScreenshotFrame frame = await read(cancellationToken);
            AfterRead?.Invoke();
            return frame;
        }

        public void Dispose()
        {
            DisposeCalls++;
            OnDispose?.Invoke();
        }
    }

    private sealed class TestBuffer : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [11, 74, 192, 255];
        internal int DisposeCalls { get; private set; }
        public Memory<byte> Memory => _bytes;
        public void Dispose() => DisposeCalls++;
    }
}

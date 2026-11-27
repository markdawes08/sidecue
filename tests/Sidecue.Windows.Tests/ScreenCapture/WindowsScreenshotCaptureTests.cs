using System.Buffers;
using Sidecue.Windows.ScreenCapture;
using Xunit;

namespace Sidecue.Windows.Tests.ScreenCapture;

public sealed class WindowsScreenshotCaptureTests
{
    [Fact]
    public async Task PickerDismissalDoesNotStartCapture()
    {
        OwnedScreenshotFrame? frame = await WindowsScreenshotCapture.CaptureSelectedAsync(
            _ => Task.FromResult<Target?>(null), (_, _) => throw new InvalidOperationException("Unexpected capture."),
            TestContext.Current.CancellationToken);

        Assert.Null(frame);
    }

    [Fact]
    public async Task CaptureUsesTheSelectedIdentityEvenWhenTargetsShareDimensions()
    {
        var otherTarget = new Target(1920, 1080);
        var selected = new Target(1920, 1080);
        using var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        Target? captured = null;

        OwnedScreenshotFrame? result = await WindowsScreenshotCapture.CaptureSelectedAsync(
            _ => Task.FromResult<Target?>(selected),
            (target, _) =>
            {
                captured = target;
                return Task.FromResult(frame);
            }, TestContext.Current.CancellationToken);

        Assert.Same(selected, captured);
        Assert.NotSame(otherTarget, captured);
        Assert.Same(frame, result);
        Assert.Equal(0, pixels.DisposeCalls);
    }

    [Fact]
    public async Task CancellationDisposesAFrameReturnedByCapture()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var pixels = new TestBuffer();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsScreenshotCapture.CaptureSelectedAsync(
            _ => Task.FromResult<Target?>(new Target(1, 1)),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(frame);
            }, cancellation.Token));

        Assert.Equal(1, pixels.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    private sealed record Target(int Width, int Height);

    private sealed class TestBuffer : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[4];
        internal int DisposeCalls { get; private set; }
        public Memory<byte> Memory => _bytes;
        public void Dispose() => DisposeCalls++;
    }
}

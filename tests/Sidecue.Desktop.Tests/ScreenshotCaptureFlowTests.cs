using System.Buffers;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Sidecue.Desktop.Prototypes.ScreenCapture;
using Sidecue.Windows.ScreenCapture;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class ScreenshotCaptureFlowTests
{
    [Fact]
    public Task PickerCancellationEndsTheFlowBeforePreview() => RunOnStaAsync(async (owner, ct) =>
    {
        var flow = new ScreenshotCaptureFlow(
            (ownerWindow, cancellationToken) =>
            {
                Assert.NotEqual(nint.Zero, ownerWindow);
                Assert.Equal(new WindowInteropHelper(owner).Handle, ownerWindow);
                Assert.Equal(ct, cancellationToken);
                return Task.FromResult<OwnedScreenshotFrame?>(null);
            },
            (_, _, _) => throw new InvalidOperationException("A canceled selection reached the preview."));

        Assert.False(await flow.PickAndPreviewAsync(owner, ct));
    });

    [Fact]
    public Task IncludedFrameIsReleasedAfterThePrototypeCheck() => RunOnStaAsync(async (owner, ct) =>
    {
        var pixels = new TrackingMemoryOwner();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var flow = new ScreenshotCaptureFlow(
            (_, _) => Task.FromResult<OwnedScreenshotFrame?>(frame),
            (previewOwner, previewFrame, cancellationToken) =>
            {
                Assert.Same(owner, previewOwner);
                Assert.Same(frame, previewFrame);
                Assert.Equal(ct, cancellationToken);
                Assert.Equal(0, pixels.DisposeCount);
                return Task.FromResult<OwnedScreenshotFrame?>(previewFrame);
            });

        Assert.True(await flow.PickAndPreviewAsync(owner, ct));
        Assert.Equal(1, pixels.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    });

    [Fact]
    public Task CancellationDuringCaptureReleasesTheReturnedFrameWithoutPreview() => RunOnStaAsync(async (owner, ct) =>
    {
        var pixels = new TrackingMemoryOwner();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flow = new ScreenshotCaptureFlow(
            (_, _) =>
            {
                operation.Cancel();
                return Task.FromResult<OwnedScreenshotFrame?>(frame);
            },
            (_, _, _) => throw new InvalidOperationException("A canceled capture reached the preview."));

        Assert.False(await flow.PickAndPreviewAsync(owner, operation.Token));
        Assert.Equal(1, pixels.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    });

    [Fact]
    public Task PreviewStartupFailureReleasesTheCapturedFrame() => RunOnStaAsync(async (owner, ct) =>
    {
        var pixels = new TrackingMemoryOwner();
        using var frame = new OwnedScreenshotFrame(1, 1, pixels);
        var flow = new ScreenshotCaptureFlow(
            (_, _) => Task.FromResult<OwnedScreenshotFrame?>(frame),
            (_, _, _) => throw new InvalidOperationException("Synthetic preview startup failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.PickAndPreviewAsync(owner, ct));
        Assert.Equal(1, pixels.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    });

    private static async Task RunOnStaAsync(Func<Window, CancellationToken, Task> test)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                var owner = new Window { Width = 320, Height = 200, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    owner.Show();
                    await test(owner, ct);
                    completion.SetResult();
                }
                catch (Exception exception) { completion.SetException(exception); }
                finally
                {
                    owner.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
    }

    private sealed class TrackingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = [11, 74, 192, 255];
        internal int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes;
        public void Dispose() => DisposeCount++;
    }
}

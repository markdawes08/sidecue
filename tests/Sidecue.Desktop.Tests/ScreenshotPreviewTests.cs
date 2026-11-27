using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidecue.Desktop.Prototypes.ScreenCapture;
using Sidecue.Windows.ScreenCapture;
using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class ScreenshotPreviewTests
{
    private static readonly byte[] SyntheticPixels =
    [
        0, 0, 255, 255, 0, 255, 0, 255,
        255, 0, 0, 255, 11, 22, 33, 255,
    ];

    [Fact]
    public Task IncludeReturnsExactlyThePreviewedFrame() => RunOnStaAsync(async (owner, ct) =>
    {
        var pixels = new TrackingMemoryOwner();
        using var frame = new OwnedScreenshotFrame(2, 2, pixels);
        Image? previewImage = null;

        OwnedScreenshotFrame? approved = await ShowAndObserveAsync(owner, frame,
            new CaptureExclusionResult(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None),
            preview =>
            {
                var image = Assert.IsType<Image>(preview.FindName("PreviewImage"));
                previewImage = image;
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                Assert.Equal(2, bitmap.PixelWidth);
                Assert.Equal(2, bitmap.PixelHeight);
                Assert.Equal(PixelFormats.Bgra32, bitmap.Format);
                var displayedPixels = new byte[SyntheticPixels.Length];
                bitmap.CopyPixels(displayedPixels, frame.Stride, 0);
                Assert.Equal(SyntheticPixels, displayedPixels);

                var status = Assert.IsType<TextBlock>(preview.FindName("CaptureStatusText"));
                Assert.Contains("Screen-sharing verification is pending.", status.Text, StringComparison.Ordinal);
                var include = Assert.IsType<Button>(preview.FindName("IncludeButton"));
                Assert.False(include.IsDefault);
                include.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, ct);

        Assert.Same(frame, approved);
        Assert.Equal(SyntheticPixels, frame.Pixels.ToArray());
        Assert.Equal(0, pixels.DisposeCount);
        Assert.Null(Assert.IsType<Image>(previewImage).Source);
        frame.Dispose();
        Assert.Equal(1, pixels.DisposeCount);
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("token")]
    public Task DismissalReleasesTheFrameWithoutApproval(string dismissal) => RunOnStaAsync(async (owner, ct) =>
    {
        var pixels = new TrackingMemoryOwner();
        using var frame = new OwnedScreenshotFrame(2, 2, pixels);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Image? previewImage = null;

        OwnedScreenshotFrame? approved = await ShowAndObserveAsync(owner, frame,
            new CaptureExclusionResult(CaptureExclusionStatus.Failed, CaptureExclusionReason.NativeCallFailed, win32Error: 5),
            preview =>
            {
                previewImage = Assert.IsType<Image>(preview.FindName("PreviewImage"));
                var status = Assert.IsType<TextBlock>(preview.FindName("CaptureStatusText"));
                Assert.Contains("Windows could not apply capture exclusion", status.Text, StringComparison.Ordinal);
                var cancel = Assert.IsType<Button>(preview.FindName("CancelButton"));
                Assert.True(cancel.IsCancel);
                switch (dismissal)
                {
                    case "cancel":
                        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        break;
                    case "close":
                        preview.Close();
                        break;
                    case "token":
                        cancellation.Cancel();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(dismissal));
                }
            }, cancellation.Token);

        Assert.Null(approved);
        Assert.Equal(1, pixels.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
        Assert.Null(Assert.IsType<Image>(previewImage).Source);
    });

    private static async Task<OwnedScreenshotFrame?> ShowAndObserveAsync(Window owner, OwnedScreenshotFrame frame,
        CaptureExclusionResult result, Action<ScreenshotPreviewWindow> interact, CancellationToken ct)
    {
        var affinity = new RecordingExclusion(owner, result, interact);
        var deadline = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(5) };
        bool timedOut = false;
        deadline.Tick += (_, _) =>
        {
            deadline.Stop();
            timedOut = true;
            owner.OwnedWindows.OfType<ScreenshotPreviewWindow>().SingleOrDefault()?.Close();
        };
        deadline.Start();
        try
        {
            OwnedScreenshotFrame? approved = await ScreenshotPreviewWindow.ShowAsync(owner, frame, affinity, ct);
            Assert.False(timedOut, "The modal preview did not receive the scripted action.");
            Task? observation = affinity.Observation;
            Assert.NotNull(observation);
            await observation;
            Assert.Equal(1, affinity.CallCount);
            return approved;
        }
        finally { deadline.Stop(); }
    }

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

    private sealed class RecordingExclusion(Window owner, CaptureExclusionResult result,
        Action<ScreenshotPreviewWindow> interact) : IWindowCaptureExclusion
    {
        internal int CallCount { get; private set; }
        internal Task? Observation { get; private set; }

        public CaptureExclusionResult Apply(nint window)
        {
            CallCount++;
            Observation = owner.Dispatcher.InvokeAsync(() =>
            {
                var preview = Assert.Single(owner.OwnedWindows.OfType<ScreenshotPreviewWindow>());
                try
                {
                    Assert.Same(owner, preview.Owner);
                    Assert.True(preview.IsVisible);
                    Assert.NotEqual((nint)0, window);
                    Assert.Equal(window, new WindowInteropHelper(preview).Handle);
                    interact(preview);
                }
                catch
                {
                    preview.Close();
                    throw;
                }
            }, DispatcherPriority.ApplicationIdle).Task;
            return result;
        }
    }

    private sealed class TrackingMemoryOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = (byte[])SyntheticPixels.Clone();
        internal int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes;
        public void Dispose() => DisposeCount++;
    }
}

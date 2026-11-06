using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Sidecue.Desktop.Prototypes.Overlay;
using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class OverlayLifecycleTests
{
    [Theory]
    [InlineData(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None, null,
        "Windows accepted the capture-exclusion request.")]
    [InlineData(CaptureExclusionStatus.Failed, CaptureExclusionReason.NativeCallFailed, 5,
        "Windows did not accept the capture-exclusion request.")]
    [InlineData(CaptureExclusionStatus.Unavailable, CaptureExclusionReason.UnsupportedWindows, null,
        "Capture exclusion is unavailable.")]
    public Task CapabilityIsAppliedToTheReadyOwnedWindowAndDisplayedWithItsLimit(
        CaptureExclusionStatus status, CaptureExclusionReason reason, int? error, string expectedText) => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var result = new CaptureExclusionResult(status, reason, win32Error: error);
        var exclusion = new RecordingExclusion(result);
        OverlayWindow? overlay = null;
        try
        {
            owner.Show();
            overlay = new OverlayWindow(owner, exclusion);
            Assert.Empty(exclusion.Handles);
            Assert.Equal(CaptureExclusionStatus.NotApplied, overlay.ViewModel.Result.Status);

            overlay.Show();
            DrainDispatcher();

            Assert.Same(owner, overlay.Owner);
            Assert.Equal(new WindowInteropHelper(overlay).Handle, Assert.Single(exclusion.Handles));
            Assert.True(exclusion.AllSourcesReady);
            Assert.Equal(result, overlay.ViewModel.Result);
            var displayed = Assert.IsType<TextBlock>(overlay.FindName("CapabilityStatusText"));
            Assert.Equal(expectedText, displayed.Text);
            Assert.True(displayed.ActualHeight > 0);
            var notice = Assert.IsType<TextBlock>(overlay.FindName("VerificationNoticeText"));
            Assert.Equal("Screen-sharing behavior has not been verified.", notice.Text);
            Assert.True(notice.ActualHeight > 0);
            if (error is not null)
            {
                var detail = Assert.IsType<TextBlock>(overlay.FindName("CapabilityDetailText"));
                Assert.Contains("5", detail.Text, StringComparison.Ordinal);
            }
        }
        finally
        {
            overlay?.Close();
            owner.Close();
        }
    });

    [Fact]
    public Task HidingReusesTheWindowButClosingCreatesAndAppliesANewWindow() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var exclusion = new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None));
        using var host = new OverlayHost(owner, exclusion);
        try
        {
            owner.Show();
            var first = host.Show();
            Assert.True(first.IsVisible);
            Assert.Single(exclusion.Handles);

            host.Hide();
            Assert.False(first.IsVisible);
            Assert.Same(first, host.Show());
            Assert.Single(exclusion.Handles);

            host.Close();
            Assert.Null(host.CurrentWindow);
            var replacement = host.Show();
            Assert.NotSame(first, replacement);
            Assert.True(replacement.IsVisible);
            Assert.Equal(2, exclusion.Handles.Count);
            Assert.True(exclusion.AllSourcesReady);
            // Windows may recycle the numerical HWND; a fresh WPF instance must
            // still apply the policy, so handle inequality is not asserted.
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task OwnerCloseClosesItsOverlayAndDisposesTheHost() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        using var host = new OverlayHost(owner,
            new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None)));
        try
        {
            owner.Show();
            var overlay = host.Show();
            var closed = false;
            overlay.Closed += (_, _) => closed = true;

            owner.Close();

            Assert.True(closed);
            Assert.False(overlay.IsVisible);
            Assert.Null(host.CurrentWindow);
            Assert.Throws<ObjectDisposedException>(() => host.Show());
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task HideButtonAndEscapeWorkWhenExclusionFails() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        using var host = new OverlayHost(owner,
            new RecordingExclusion(new(CaptureExclusionStatus.Failed, CaptureExclusionReason.NativeCallFailed, win32Error: 5)));
        try
        {
            owner.Show();
            var overlay = host.Show();
            DrainDispatcher();
            var hide = Assert.IsType<Button>(overlay.FindName("HideOverlayButton"));
            Assert.True(hide.IsEnabled);
            hide.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsVisible);

            Assert.Same(overlay, host.Show());
            var source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(overlay));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            overlay.RaiseEvent(escape);

            Assert.True(escape.Handled);
            Assert.False(overlay.IsVisible);
        }
        finally { owner.Close(); }
    });

    private static Window CreateOwner() => new()
    {
        Title = "Synthetic overlay test owner",
        Width = 300,
        Height = 200,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static async Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private sealed class RecordingExclusion(CaptureExclusionResult result) : IWindowCaptureExclusion
    {
        internal List<nint> Handles { get; } = [];
        internal bool AllSourcesReady { get; private set; } = true;

        public CaptureExclusionResult Apply(nint hwnd)
        {
            Handles.Add(hwnd);
            AllSourcesReady &= hwnd != 0 && HwndSource.FromHwnd(hwnd) is not null;
            return result;
        }
    }
}

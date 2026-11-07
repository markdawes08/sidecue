using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    public Task OwnerCloseClosesOverlayAndDetailsAndDisposesTheHost() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        using var host = new OverlayHost(owner,
            new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None)));
        try
        {
            owner.Show();
            var overlay = host.Show();
            var details = host.ShowDetails();
            var closed = false;
            var detailsClosed = false;
            overlay.Closed += (_, _) => closed = true;
            details.Closed += (_, _) => detailsClosed = true;

            owner.Close();

            Assert.True(closed);
            Assert.True(detailsClosed);
            Assert.False(overlay.IsVisible);
            Assert.False(details.IsVisible);
            Assert.Null(host.CurrentWindow);
            Assert.Null(host.DetailsWindow);
            Assert.Throws<ObjectDisposedException>(() => host.Show());
            Assert.Throws<ObjectDisposedException>(() => host.ShowDetails());
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
            var details = host.ShowDetails();
            DrainDispatcher();
            var hide = Assert.IsType<Button>(overlay.FindName("HideOverlayButton"));
            Assert.True(hide.IsEnabled);
            hide.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(overlay.IsVisible);
            Assert.False(details.IsVisible);

            Assert.Same(overlay, host.Show());
            Assert.Same(details, host.ShowDetails());
            var source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(overlay));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            overlay.RaiseEvent(escape);

            Assert.True(escape.Handled);
            Assert.False(overlay.IsVisible);
            Assert.False(details.IsVisible);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task StreamingUpdatesPreserveVisibilityAndInputModeUntilAnExplicitCommand() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var activation = new RecordingActivation();
        var exclusion = new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None));
        using var host = new OverlayHost(owner, exclusion, activation);
        try
        {
            owner.Show();
            host.UpdateDisplay("Synthetic first answer");
            Assert.Null(host.CurrentWindow);

            var overlay = host.Show();
            DrainDispatcher();
            var handle = new WindowInteropHelper(overlay).Handle;
            var input = Assert.IsType<TextBox>(overlay.FindName("PrototypeInput"));
            Assert.Equal(OverlayMode.Display, overlay.ViewModel.Mode);
            Assert.Equal("Synthetic first answer", overlay.ViewModel.DisplayText);
            Assert.False(overlay.ShowActivated);
            Assert.False(input.IsEnabled);
            Assert.False(input.Focusable);
            var policyCalls = activation.InteractiveRequests.Count;
            host.UpdateDisplay("Synthetic display update");
            DrainDispatcher();
            Assert.Equal(OverlayMode.Display, overlay.ViewModel.Mode);
            Assert.Equal(policyCalls, activation.InteractiveRequests.Count);

            Assert.Same(overlay, host.ShowInteractive());
            DrainDispatcher();
            Assert.Equal(OverlayMode.Interactive, overlay.ViewModel.Mode);
            Assert.True(input.IsEnabled);
            Assert.True(input.Focusable);
            input.Text = "Synthetic local draft";
            policyCalls = activation.InteractiveRequests.Count;

            host.UpdateDisplay("Synthetic revised answer");
            DrainDispatcher();
            Assert.Equal(OverlayMode.Interactive, overlay.ViewModel.Mode);
            Assert.Equal(policyCalls, activation.InteractiveRequests.Count);
            Assert.Equal("Synthetic local draft", input.Text);
            Assert.Equal("Synthetic revised answer", overlay.ViewModel.DisplayText);
            Assert.Equal(handle, new WindowInteropHelper(overlay).Handle);

            Assert.Same(overlay, host.Show());
            DrainDispatcher();
            Assert.Equal(OverlayMode.Display, overlay.ViewModel.Mode);
            Assert.False(input.IsEnabled);
            Assert.False(input.Focusable);
            host.Hide();
            policyCalls = activation.InteractiveRequests.Count;
            host.UpdateDisplay("Synthetic hidden update");
            DrainDispatcher();
            Assert.False(overlay.IsVisible);
            Assert.Equal(policyCalls, activation.InteractiveRequests.Count);
            Assert.Equal("Synthetic hidden update", overlay.ViewModel.DisplayText);
            Assert.Single(exclusion.Handles);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task PlacementChangesRetainTheExcludedWindow() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var activation = new RecordingActivation();
        var exclusion = new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None));
        using var host = new OverlayHost(owner, exclusion, activation);
        try
        {
            owner.Show();
            var overlay = host.Show();
            DrainDispatcher();
            var handle = new WindowInteropHelper(overlay).Handle;
            var policyCalls = activation.InteractiveRequests.Count;

            overlay.Left += 24;
            overlay.Top += 18;
            overlay.Width += 40;
            overlay.Height += 20;
            DrainDispatcher();

            Assert.Same(overlay, host.CurrentWindow);
            Assert.Equal(handle, new WindowInteropHelper(overlay).Handle);
            Assert.Equal(policyCalls, activation.InteractiveRequests.Count);
            Assert.Single(exclusion.Handles);
            Assert.Equal(OverlayMode.Display, overlay.ViewModel.Mode);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task DetailsApplyExclusionToTheirOwnHandleAndExposeFailure() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var result = new CaptureExclusionResult(CaptureExclusionStatus.Failed,
            CaptureExclusionReason.NativeCallFailed, win32Error: 5);
        var exclusion = new RecordingExclusion(result);
        using var host = new OverlayHost(owner, exclusion, new RecordingActivation());
        try
        {
            owner.Show();
            var overlay = host.Show();
            var details = host.ShowDetails();
            DrainDispatcher();

            Assert.Same(overlay, details.Owner);
            Assert.Equal(2, exclusion.Handles.Count);
            Assert.NotEqual(exclusion.Handles[0], exclusion.Handles[1]);
            Assert.True(exclusion.AllSourcesReady);
            Assert.Equal(result, details.ViewModel.Result);
            var status = Assert.IsType<TextBlock>(details.FindName("CapabilityStatusText"));
            Assert.Equal("Windows did not accept the capture-exclusion request.", status.Text);
            var detail = Assert.IsType<TextBlock>(details.FindName("CapabilityDetailText"));
            Assert.Contains("5", detail.Text, StringComparison.Ordinal);
            AssertScrollBarPopupsDisabled(overlay);
            AssertScrollBarPopupsDisabled(details);

            host.Hide();
            Assert.False(overlay.IsVisible);
            Assert.False(details.IsVisible);
            Assert.Same(details, host.ShowDetails());
            Assert.Equal(2, exclusion.Handles.Count);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task FailedInteractivePolicyKeepsInputDisabledAndDisplaysTheError() => RunOnStaAsync(() =>
    {
        var owner = CreateOwner();
        var activation = new RecordingActivation { FailInteractive = true };
        using var host = new OverlayHost(owner,
            new RecordingExclusion(new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None)), activation);
        try
        {
            owner.Show();
            var overlay = host.ShowInteractive();
            DrainDispatcher();

            Assert.Contains(true, activation.InteractiveRequests);
            Assert.Equal(OverlayMode.Display, overlay.ViewModel.Mode);
            Assert.False(overlay.IsVisible);
            Assert.Equal(overlay.ViewModel.ActivationStatusText, host.StatusText);
            var input = Assert.IsType<TextBox>(overlay.FindName("PrototypeInput"));
            Assert.False(input.IsEnabled);
            Assert.False(input.Focusable);
            Assert.False(overlay.ShowActivated);
            Assert.Contains("5", overlay.ViewModel.ActivationStatusText, StringComparison.Ordinal);
            var status = Assert.IsType<TextBlock>(overlay.FindName("ActivationStatusText"));
            Assert.Equal(overlay.ViewModel.ActivationStatusText, status.Text);
        }
        finally { owner.Close(); }
    });

    private static void AssertScrollBarPopupsDisabled(Window window)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(window);
        var scrollBars = new List<ScrollBar>();
        while (pending.TryPop(out var element))
        {
            if (element is ScrollBar scrollBar) { scrollBars.Add(scrollBar); }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                pending.Push(VisualTreeHelper.GetChild(element, index));
            }
        }
        Assert.NotEmpty(scrollBars);
        Assert.All(scrollBars, scrollBar =>
        {
            Assert.False(ContextMenuService.GetIsEnabled(scrollBar));
            Assert.False(ToolTipService.GetIsEnabled(scrollBar));
        });
    }

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

    private sealed class RecordingActivation : IWindowActivationPolicy
    {
        internal List<bool> InteractiveRequests { get; } = [];
        internal bool FailInteractive { get; init; }

        public WindowOperationResult Apply(nint window, bool interactive)
        {
            Assert.NotEqual(0, window);
            InteractiveRequests.Add(interactive);
            return FailInteractive && interactive ? new(false, 5) : new(true, 0);
        }
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

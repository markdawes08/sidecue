using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Owned prototype window. API acceptance is distinct from observer-side verification.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Window lifetime; OnClosed releases its HWND message hook.")]
public sealed partial class OverlayWindow : Window
{
    private readonly OverlaySurfacePolicy _policy;

    public OverlayWindow(Window owner, IWindowCaptureExclusion exclusion, IWindowActivationPolicy? activation = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exclusion);
        owner.Dispatcher.VerifyAccess();
        InitializeComponent();
        Owner = owner;
        ViewModel = new OverlayViewModel();
        DataContext = ViewModel;
        _policy = new OverlaySurfacePolicy(this, exclusion, activation ?? new WindowActivationPolicy());
    }

    public OverlayViewModel ViewModel { get; }
    public event EventHandler? HideRequested;

    public void ShowDisplay()
    {
        Dispatcher.VerifyAccess();
        EnsureMode(OverlayMode.Display);
        if (ViewModel.ActivationResult is { Succeeded: true }) { Show(); }
    }

    public void ShowInteractive()
    {
        Dispatcher.VerifyAccess();
        EnsureMode(OverlayMode.Interactive);
        if (ViewModel.ActivationResult is not { Succeeded: true }) { return; }
        Show();
        // Only this deliberate entry point asks Windows for foreground focus.
        if (Activate()) { PrototypeInput.Focus(); }
        else { ViewModel.ReportActivationDenied(); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ViewModel.SetResult(_policy.Initialize());
        ViewModel.SetActivationResult(OverlayMode.Display, _policy.SetInteractive(false));
    }

    protected override void OnClosed(EventArgs e)
    {
        _policy.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            RequestHide();
            e.Handled = true;
        }
    }

    private void EnsureMode(OverlayMode mode)
    {
        new WindowInteropHelper(this).EnsureHandle();
        ViewModel.SetActivationResult(mode, _policy.SetInteractive(mode == OverlayMode.Interactive));
        if (!ViewModel.IsInteractive && IsKeyboardFocusWithin) { Keyboard.ClearFocus(); }
        if (ViewModel.ActivationResult is { Succeeded: false }) { RequestHide(); }
    }

    private void RequestHide()
    {
        if (HideRequested is { } handler) { handler(this, EventArgs.Empty); }
        else { Hide(); }
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => RequestHide();

    private void OnMoveGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) { DragMove(); }
    }
}

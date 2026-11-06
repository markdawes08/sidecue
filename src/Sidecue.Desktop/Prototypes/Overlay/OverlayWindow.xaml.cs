using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Owned prototype window. API acceptance is distinct from observer-side verification.</summary>
public sealed partial class OverlayWindow : Window
{
    private readonly IWindowCaptureExclusion _exclusion;

    public OverlayWindow(Window owner, IWindowCaptureExclusion exclusion)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exclusion);
        owner.Dispatcher.VerifyAccess();
        _exclusion = exclusion;
        InitializeComponent();
        Owner = owner;
        ViewModel = new OverlayViewModel();
        DataContext = ViewModel;
    }

    public OverlayViewModel ViewModel { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Each newly created Window has its own lifecycle. Never cache policy by
        // numeric handle: Windows can reuse that number after a prior close.
        ViewModel.SetResult(_exclusion.Apply(new WindowInteropHelper(this).Handle));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
}

using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Sidecue.Desktop.Prototypes.Overlay;
using Sidecue.Desktop.ViewModels;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Window lifetime; OnClosed disposes the overlay host.")]
public partial class MainWindow : Window
{
    private readonly OverlayHost _overlay;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        _overlay = new OverlayHost(this, new WindowCaptureExclusion());
    }

    private void ShowOverlayClicked(object sender, RoutedEventArgs e) => _overlay.Show();
    private void HideOverlayClicked(object sender, RoutedEventArgs e) => _overlay.Hide();

    protected override void OnClosed(EventArgs e)
    {
        _overlay.Dispose();
        base.OnClosed(e);
    }
}

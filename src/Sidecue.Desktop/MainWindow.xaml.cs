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
    private OverlayKeyboardController? _keyboard;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        _overlay = new OverlayHost(this, new WindowCaptureExclusion());
        _overlay.StatusChanged += OnOverlayStatusChanged;
    }

    private void ShowOverlayClicked(object sender, RoutedEventArgs e) => _overlay.Show();
    private void EnterOverlayClicked(object sender, RoutedEventArgs e) => _overlay.ShowInteractive();
    private void OverlayDetailsClicked(object sender, RoutedEventArgs e) => _overlay.ShowDetails();
    private void HideOverlayClicked(object sender, RoutedEventArgs e) => _overlay.Hide();

    private void OnOverlayStatusChanged(object? sender, EventArgs e) => OverlayStatusText.Text = _overlay.StatusText;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _keyboard = new OverlayKeyboardController(this, _overlay);
        HotkeyStatusText.Text = _keyboard.StatusText;
    }

    protected override void OnClosed(EventArgs e)
    {
        _keyboard?.Dispose();
        _overlay.StatusChanged -= OnOverlayStatusChanged;
        _overlay.Dispose();
        base.OnClosed(e);
    }
}

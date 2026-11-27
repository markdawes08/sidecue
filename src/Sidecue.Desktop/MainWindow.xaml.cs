using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Sidecue.Desktop.Prototypes.Overlay;
using Sidecue.Desktop.Prototypes.ScreenCapture;
using Sidecue.Desktop.ViewModels;
using Sidecue.Windows.ScreenCapture;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Window lifetime. OnClosed disposes the overlay host and cancels the screenshot operation.")]
public partial class MainWindow : Window
{
    private readonly OverlayHost _overlay;
    private readonly ScreenshotCaptureFlow _screenshotFlow = new();
    private OverlayKeyboardController? _keyboard;
    private CancellationTokenSource? _screenshotOperation;

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

    private async void CaptureScreenshotClicked(object sender, RoutedEventArgs e)
    {
        if (_screenshotOperation is not null)
        {
            return;
        }

        using var operation = new CancellationTokenSource();
        _screenshotOperation = operation;
        CaptureScreenshotButton.IsEnabled = false;
        ScreenshotStatusText.Text = "Choose a window or display.";
        try
        {
            bool included = await _screenshotFlow.PickAndPreviewAsync(this, operation.Token);
            ShowScreenshotStatus(included ? "Screenshot included in the preview test." : "Screenshot canceled.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // Closing the owner ends the in-flight operation.
        }
        catch (ScreenshotCaptureException exception)
        {
            ShowScreenshotStatus(exception.Message);
        }
        catch (Exception)
        {
            ShowScreenshotStatus("The screenshot preview could not be opened.");
        }
        finally
        {
            _screenshotOperation = null;
            if (!operation.IsCancellationRequested)
            {
                CaptureScreenshotButton.IsEnabled = true;
            }
        }
    }

    private void ShowScreenshotStatus(string message)
    {
        if (_screenshotOperation is { IsCancellationRequested: false })
        {
            ScreenshotStatusText.Text = message;
        }
    }

    private void OnOverlayStatusChanged(object? sender, EventArgs e) => OverlayStatusText.Text = _overlay.StatusText;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _keyboard = new OverlayKeyboardController(this, _overlay);
        HotkeyStatusText.Text = _keyboard.StatusText;
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshotOperation?.Cancel();
        _keyboard?.Dispose();
        _overlay.StatusChanged -= OnOverlayStatusChanged;
        _overlay.Dispose();
        base.OnClosed(e);
    }
}

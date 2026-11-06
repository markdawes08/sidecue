using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Explicit local prototype probe. It makes no observer-side capture assertion.</summary>
internal static class OverlayProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var observations = new Dictionary<string, object?>
        {
            ["Probe"] = "SC-002A-local-overlay",
            ["RecordedUtc"] = DateTimeOffset.UtcNow,
            ["OsDescription"] = RuntimeInformation.OSDescription,
            ["Runtime"] = RuntimeInformation.FrameworkDescription,
            ["Is64BitProcess"] = Environment.Is64BitProcess,
            ["UserInteractive"] = Environment.UserInteractive,
            ["RemoteCaptureVerified"] = false
        };
        var exitCode = 0;
        Window? owner = null;
        OverlayHost? host = null;
        try
        {
            if (!Environment.UserInteractive) { throw new InvalidOperationException("An interactive Windows desktop is required."); }
            var native = new Win32WindowAffinityApi();
            var composition = native.GetCompositionStatus();
            observations["CompositionEnabled"] = composition.Enabled;
            observations["CompositionHResult"] = composition.HResult;
            owner = new Window
            {
                Title = "Sidecue local probe owner",
                Width = 480, Height = 180,
                ShowInTaskbar = false,
                Content = new TextBlock { Text = "Synthetic overlay lifecycle probe", Margin = new Thickness(24) }
            };
            owner.Show();
            var recording = new RecordingExclusion(new WindowCaptureExclusion(native));
            host = new OverlayHost(owner, recording);
            var first = host.Show();
            await SettleAsync(first);
            var firstHandle = new WindowInteropHelper(first).Handle;
            if (firstHandle == 0 || first.Owner != owner) { throw new InvalidOperationException("The overlay must have an owned HWND."); }
            observations["OwnedWindowCreated"] = true;
            observations["FirstApply"] = Describe(first.ViewModel.Result);
            observations["FirstReadback"] = Win32WindowAffinityApi.ReadDisplayAffinity(firstHandle);
            observations["AllowsTransparency"] = first.AllowsTransparency;
            observations["WindowStyle"] = first.WindowStyle.ToString();
            observations["DpiScaleX"] = VisualTreeHelper.GetDpi(first).DpiScaleX;
            SavePreview(first, Path.Combine(outputDirectory, "overlay-preview.png"));
            host.Hide();
            if (first.IsVisible) { throw new InvalidOperationException("Hide did not hide the overlay."); }
            observations["HideWorked"] = true;
            var reshown = host.Show();
            await SettleAsync(reshown);
            if (!ReferenceEquals(first, reshown) || new WindowInteropHelper(reshown).Handle != firstHandle || recording.Calls != 1)
            {
                throw new InvalidOperationException("Hide/show must retain the current window without a duplicate affinity application.");
            }
            observations["HideShowRetainedWindow"] = true;
            host.Close();
            var replacement = host.Show();
            await SettleAsync(replacement);
            if (ReferenceEquals(first, replacement) || new WindowInteropHelper(replacement).Handle == 0 || recording.Calls != 2)
            {
                throw new InvalidOperationException("A recreated overlay must apply affinity to its fresh HWND lifetime.");
            }
            observations["RecreatedWindowAppliedAgain"] = true;
            observations["SecondApply"] = Describe(replacement.ViewModel.Result);
            observations["SecondReadback"] = Win32WindowAffinityApi.ReadDisplayAffinity(new WindowInteropHelper(replacement).Handle);
            var hide = (Button)replacement.FindName("HideOverlayButton");
            hide.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (replacement.IsVisible) { throw new InvalidOperationException("The overlay hide control failed."); }
            observations["HideControlWorked"] = true;
            owner.Close();
            if (host.CurrentWindow is not null) { throw new InvalidOperationException("Owner closure did not release its overlay."); }
            observations["OwnerCloseReleasedOverlay"] = true;
            observations["AffinityApplyCount"] = recording.Calls;
            observations["LifecyclePassed"] = true;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            observations["LifecyclePassed"] = false;
            observations["FailureType"] = exception.GetType().Name;
            observations["Failure"] = exception.Message;
        }
        finally
        {
            host?.Dispose();
            owner?.Close();
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "native-probe.json"), JsonSerializer.Serialize(observations, JsonOptions));
        return exitCode;
    }

    private static object Describe(CaptureExclusionResult result) => new
    {
        Status = result.Status.ToString(), Reason = result.Reason.ToString(), result.Win32Error, result.HResult
    };

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void SavePreview(Window window, string path)
    {
        // Render only this synthetic WPF content. This is not a screen capture/exclusion test.
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // The content Grid inherits the window background but does not paint it.
        // Include that background and normalize layout offsets in the local render.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        png.Save(output);
    }

    private sealed class RecordingExclusion(IWindowCaptureExclusion inner) : IWindowCaptureExclusion
    {
        internal int Calls { get; private set; }
        public CaptureExclusionResult Apply(nint hwnd)
        {
            Calls++;
            return inner.Apply(hwnd);
        }
    }
}

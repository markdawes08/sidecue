using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Sidecue.Windows.ScreenCapture;
using Windows.Graphics.Capture;
using WinRT;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class ScreenshotNativeProbeTests
{
    private static readonly Guid CaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(3);

    [ScreenshotNativeProbeFact]
    public Task CapturesOnlyTheSyntheticWindow() => RunOnStaAsync(async (handle, cancellationToken) =>
    {
        // Request exactly the test's own HWND. No desktop or user window is a capture candidate.
        GraphicsCaptureItem item = CreateItemForWindow(handle);
        var size = item.Size;
        using OwnedScreenshotFrame frame = await WindowsGraphicsCaptureFrame.CaptureAsync(item, cancellationToken);

        Assert.Equal(size.Width, frame.Width);
        Assert.Equal(size.Height, frame.Height);
        Assert.Equal(frame.Width * 4, frame.Stride);
        Assert.InRange(frame.Width, 32, 8192);
        Assert.InRange(frame.Height, 32, 8192);
        AssertPixel(frame, frame.Width / 4, frame.Height / 2, Brushes.RoyalBlue.Color);
        AssertPixel(frame, frame.Width * 3 / 4, frame.Height / 2, Brushes.Goldenrod.Color);
    });

    [ScreenshotNativeProbeFact]
    public Task OpensAndCancelsTheWindowsPicker()
    {
        string ownerTitle = "Sidecue picker " + Guid.NewGuid().ToString("N")[..8];
        return RunOnStaAsync((handle, cancellationToken) => ObservePickerCancellationAsync(handle, ownerTitle,
            cancellationToken), ownerTitle);
    }

    private static async Task ObservePickerCancellationAsync(nint owner, string ownerTitle,
        CancellationToken cancellationToken)
    {
        Assert.True(await ObserveAsync(() => GetForegroundWindow() == owner, ForegroundTimeout, cancellationToken),
            "The synthetic picker owner did not become the foreground window.");

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<GraphicsCaptureItem?> selection = WindowsScreenshotCapture.PickTargetAsync(owner, request.Token);
        try
        {
            nint picker = 0;
            var caption = new char[256];
            bool observed = await ObserveAsync(() => selection.IsCompleted ||
                (picker = FindForegroundPicker(owner, ownerTitle, caption)) != 0, ObservationTimeout, cancellationToken);
            if (selection.IsCompleted)
            {
                GraphicsCaptureItem? earlyItem = await selection;
                Assert.Fail(earlyItem is null
                    ? "The picker returned no item before the probe requested cancellation."
                    : "The picker returned an unexpected item before the probe requested cancellation.");
            }
            Assert.True(observed, "The picker for the synthetic owner did not become visible in the foreground.");

            request.Cancel();
            try
            {
                GraphicsCaptureItem? item = await selection.WaitAsync(ObservationTimeout, cancellationToken);
                Assert.True(item is null, "The picker returned an unexpected item after cancellation.");
            }
            catch (OperationCanceledException) when (selection.IsCanceled && !cancellationToken.IsCancellationRequested)
            {
                // The WinRT operation can complete with no item or be canceled.
            }

            Assert.True(await ObserveAsync(() => !IsWindowVisible(picker), ObservationTimeout, cancellationToken),
                "The picker stayed visible after cancellation completed.");
        }
        finally
        {
            request.Cancel();
        }
    }

    private static nint FindForegroundPicker(nint owner, string ownerTitle, char[] caption)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == owner || !IsWindowVisible(foreground))
        {
            return 0;
        }

        // Explorer's picker caption contains the owner's title. A per-run title
        // distinguishes this picker without relying on a localized prefix.
        int length = GetWindowTextW(foreground, caption, caption.Length);
        return caption.AsSpan(0, length).Contains(ownerTitle.AsSpan(), StringComparison.Ordinal) ? foreground : 0;
    }

    private static async Task<bool> ObserveAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) { return true; }
            if (elapsed.Elapsed >= timeout) { return false; }
            await Task.Delay(50, cancellationToken);
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint window)
    {
        using IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", CaptureItemInteropId);
        nint vtable = Marshal.ReadIntPtr(factory.ThisPtr);
        // CreateForWindow is the first IGraphicsCaptureItemInterop method after IUnknown.
        var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(create(factory.ThisPtr, window, in CaptureItemId, out item));
            return Assert.IsType<GraphicsCaptureItem>(MarshalInspectable<GraphicsCaptureItem>.FromAbi(item));
        }
        finally
        {
            MarshalInspectable<GraphicsCaptureItem>.DisposeAbi(item);
        }
    }

    private static void AssertPixel(OwnedScreenshotFrame frame, int x, int y, Color expected)
    {
        ReadOnlySpan<byte> pixel = frame.Pixels.Span.Slice(y * frame.Stride + x * 4, 4);
        const int roundingTolerance = 12;
        Assert.InRange(Math.Abs(pixel[0] - expected.B), 0, roundingTolerance);
        Assert.InRange(Math.Abs(pixel[1] - expected.G), 0, roundingTolerance);
        Assert.InRange(Math.Abs(pixel[2] - expected.R), 0, roundingTolerance);
    }

    private static async Task RunOnStaAsync(Func<nint, CancellationToken, Task> test, string? pickerOwnerTitle = null)
    {
        Assert.True(Environment.UserInteractive, "The screenshot probe requires an interactive Windows desktop.");
        Assert.True(GraphicsCaptureSession.IsSupported(), "Windows Graphics Capture is unavailable on this desktop.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        CancellationToken cancellationToken = deadline.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var window = CreateWindow(pickerOwnerTitle);
                    var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.ContentRendered += (_, _) => rendered.TrySetResult();
                    try
                    {
                        window.Show();
                        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                        nint handle = new WindowInteropHelper(window).Handle;
                        Assert.NotEqual(0, handle);
                        await test(handle, cancellationToken);
                    }
                    finally
                    {
                        window.Close();
                    }
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(27), TestContext.Current.CancellationToken);
    }

    private static Window CreateWindow(string? pickerOwnerTitle)
    {
        var colors = new Grid();
        colors.ColumnDefinitions.Add(new ColumnDefinition());
        colors.ColumnDefinitions.Add(new ColumnDefinition());
        colors.Children.Add(new Border { Background = Brushes.RoyalBlue });
        var right = new Border { Background = Brushes.Goldenrod };
        Grid.SetColumn(right, 1);
        colors.Children.Add(right);

        return new Window
        {
            Title = pickerOwnerTitle ?? "Sidecue synthetic screenshot target",
            Width = 320,
            Height = 180,
            Content = colors,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = pickerOwnerTitle is not null,
            ShowActivated = true,
        };
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextW(nint window, [Out] char[] text, int textCapacity);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint factory, nint window, in Guid iid, out nint item);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ScreenshotNativeProbeFactAttribute : FactAttribute
{
    public ScreenshotNativeProbeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("SIDECUE_RUN_SCREENSHOT_NATIVE_PROBE") != "1")
        {
            Skip = "Set SIDECUE_RUN_SCREENSHOT_NATIVE_PROBE=1 on an interactive Windows desktop.";
        }
    }
}

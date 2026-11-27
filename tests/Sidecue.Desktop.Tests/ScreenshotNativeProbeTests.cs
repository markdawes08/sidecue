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
    public Task OpensAndCancelsTheWindowsPicker() => RunOnStaAsync(async (handle, cancellationToken) =>
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<GraphicsCaptureItem?> selection = WindowsScreenshotCapture.PickTargetAsync(handle, request.Token);
        try
        {
            // Allow the real picker to open while the STA continues pumping messages.
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            if (selection.IsCompleted)
            {
                _ = await selection;
                Assert.Fail("The system picker closed before the probe requested cancellation.");
            }

            request.Cancel();
            GraphicsCaptureItem? item = await selection.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
            Assert.Null(item);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Assert.True(selection.IsCanceled);
        }
        finally
        {
            request.Cancel();
        }
    });

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

    private static async Task RunOnStaAsync(Func<nint, CancellationToken, Task> test)
    {
        Assert.True(Environment.UserInteractive, "The screenshot probe requires an interactive Windows desktop.");
        Assert.True(GraphicsCaptureSession.IsSupported(), "Windows Graphics Capture is unavailable on this desktop.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        CancellationToken cancellationToken = deadline.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var window = CreateWindow();
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private static Window CreateWindow()
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
            Title = "Sidecue synthetic screenshot target",
            Width = 320,
            Height = 180,
            Content = colors,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = true,
        };
    }

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

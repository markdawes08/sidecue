using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sidecue.Desktop.Prototypes.Overlay;
using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Desktop.Tests;

public sealed class OverlayForegroundTests
{
    [OverlayForegroundFact]
    public async Task DisplayUpdatesPreserveSeparateForegroundWindow()
    {
        Assert.True(Environment.UserInteractive, "The foreground probe requires an interactive Windows desktop.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string eventName = $"Local\\SidecueOverlayFocus-{Guid.NewGuid():N}";
        using var focusLost = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using Process target = LaunchTarget(eventName);
        try
        {
            nint targetWindow = await WaitForForegroundAsync(target, ct);
            await ObserveFromDispatcherAsync(targetWindow, focusLost, ct);
        }
        finally
        {
            if (!target.HasExited && (!target.CloseMainWindow() || !target.WaitForExit(2000)))
            {
                if (!target.HasExited)
                {
                    target.Kill();
                    target.WaitForExit(2000);
                }
            }
        }
    }

    private static Process LaunchTarget(string eventName)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Show-OverlayFocusTarget.ps1");
        var startInfo = new ProcessStartInfo(Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-File", script, "-LostFocusEvent", eventName })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the foreground target.");
    }

    private static async Task<nint> WaitForForegroundAsync(Process target, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (!target.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(22))
        {
            target.Refresh();
            nint window = target.MainWindowHandle;
            if (window != 0 && GetForegroundWindow() == window)
            {
                return window;
            }
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException("Windows did not give foreground focus to the test-owned target.");
    }

    private static async Task ObserveFromDispatcherAsync(nint targetWindow, EventWaitHandle focusLost, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ObserveUpdatesAsync(targetWindow, focusLost, ct);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
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
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
    }

    private static async Task ObserveUpdatesAsync(nint targetWindow, EventWaitHandle focusLost, CancellationToken ct)
    {
        focusLost.Reset();
        CheckForeground(targetWindow, focusLost, "before showing the overlay");
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false, Width = 1, Height = 1, Opacity = 0 };
        using var host = new OverlayHost(owner, new WindowCaptureExclusion(new Win32WindowAffinityApi()));
        try
        {
            owner.Show();
            OverlayWindow overlay = host.Show();
            await overlay.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, ct);
            Assert.True(overlay.IsVisible);
            Assert.False(overlay.ViewModel.IsInteractive);
            CheckForeground(targetWindow, focusLost, "after showing the overlay");

            string[] updates = ["Synthetic display one", "Synthetic display two", "Synthetic display three", "Synthetic display four"];
            foreach (string update in updates)
            {
                ct.ThrowIfCancellationRequested();
                host.UpdateDisplay(update);
                await overlay.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, ct);
                Assert.True(ContainsText(overlay, update), "The overlay did not display the synthetic update.");
                CheckForeground(targetWindow, focusLost, "after a display update");
                await Task.Delay(50, ct);
            }
            CheckForeground(targetWindow, focusLost, "after all display updates settled");
            host.Hide();
            await overlay.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, ct);
            Assert.False(overlay.IsVisible);
            CheckForeground(targetWindow, focusLost, "after hiding the overlay");
        }
        finally
        {
            owner.Close();
        }
    }

    private static bool ContainsText(DependencyObject parent, string expected)
    {
        if (parent is TextBlock { Text: var text, IsVisible: true } && string.Equals(text, expected, StringComparison.Ordinal))
        {
            return true;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (ContainsText(VisualTreeHelper.GetChild(parent, index), expected))
            {
                return true;
            }
        }
        return false;
    }

    private static void CheckForeground(nint window, EventWaitHandle focusLost, string stage)
    {
        Assert.True(GetForegroundWindow() == window && !focusLost.WaitOne(0),
            $"The test-owned target lost foreground focus {stage}.");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class OverlayForegroundFactAttribute : FactAttribute
{
    public OverlayForegroundFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("SIDECUE_RUN_OVERLAY_FOCUS_PROBE") != "1")
        {
            Skip = "Set SIDECUE_RUN_OVERLAY_FOCUS_PROBE=1 for the interactive foreground probe.";
        }
    }
}

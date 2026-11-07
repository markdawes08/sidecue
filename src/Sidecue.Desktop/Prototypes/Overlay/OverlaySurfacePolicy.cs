using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Applies the same HWND policy to the overlay and every prototype auxiliary surface.</summary>
internal sealed class OverlaySurfacePolicy(
    Window window, IWindowCaptureExclusion exclusion, IWindowActivationPolicy activation) : IDisposable
{
    private HwndSource? _source;
    private bool _interactive;

    internal CaptureExclusionResult Initialize()
    {
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
        window.ContextMenuOpening += OnContextMenuOpening;
        window.ToolTipOpening += OnToolTipOpening;
        // A new WPF Window always reapplies affinity, even if Windows recycles a handle.
        return exclusion.Apply(handle);
    }

    internal WindowOperationResult SetInteractive(bool interactive)
    {
        // The message guard remains non-activating if the native operation fails.
        _interactive = false;
        var result = activation.Apply(new WindowInteropHelper(window).Handle, interactive);
        if (result.Succeeded) { _interactive = interactive; }
        return result;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WindowMessage);
        _source = null;
        window.ContextMenuOpening -= OnContextMenuOpening;
        window.ToolTipOpening -= OnToolTipOpening;
        GC.SuppressFinalize(this);
    }

    // These routed events cover template children too. Attached IsEnabled values
    // on Window alone do not inherit to controls such as ScrollBar.
    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;
    private static void OnToolTipOpening(object sender, ToolTipEventArgs e) => e.Handled = true;

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int MouseActivate = 0x0021;
        const int NoActivate = 3;
        const int SystemCommand = 0x0112;
        const int MouseMenu = 0xF090;
        const int KeyMenu = 0xF100;
        // WPF retains WS_SYSMENU even with WindowStyle=None. Its native menu
        // would be another HWND without our affinity policy, so suppress it.
        if (message == SystemCommand && ((long)wParam & 0xFFF0) is MouseMenu or KeyMenu)
        {
            handled = true;
            return 0;
        }
        if (message == MouseActivate && !_interactive)
        {
            handled = true;
            return NoActivate;
        }
        return 0;
    }
}

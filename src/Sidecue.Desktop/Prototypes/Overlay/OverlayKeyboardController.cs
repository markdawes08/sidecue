using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Routes only successfully registered shortcuts on the owner's dispatcher.</summary>
public sealed class OverlayKeyboardController : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private readonly Window _owner;
    private readonly OverlayHost _overlay;
    private readonly OverlayHotkeyRegistration _registration;
    private readonly HwndSource _source;
    private bool _disposed;

    public OverlayKeyboardController(Window owner, OverlayHost overlay,
        OverlayHotkeyRegistration? registration = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(overlay);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _overlay = overlay;
        var handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("The shortcut owner must have an initialized HWND.");
        _registration = registration ?? new OverlayHotkeyRegistration();
        Results = _registration.Register(handle);
        _source.AddHook(OnMessage);
        _owner.Closed += OnOwnerClosed;
    }

    public IReadOnlyList<OverlayHotkeyResult> Results { get; }

    public string StatusText => string.Join(Environment.NewLine, Results.Select(result =>
        result.Succeeded
            ? $"{result.Binding.Shortcut}: {Describe(result.Binding.Action)}"
            : $"{result.Binding.Shortcut}: unavailable (Windows error {result.Win32Error.ToString(CultureInfo.InvariantCulture)}). Another app may be using it. Use the buttons below."));

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) { return; }
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        _source.RemoveHook(OnMessage);
        _registration.Dispose();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF Closed runs after HWND teardown. Release while the handle is valid;
        // unlike Closing, WM_DESTROY cannot subsequently be cancelled.
        if (message == 0x0002)
        {
            Dispose();
            return 0;
        }
        if (_disposed || message != HotkeyMessage ||
            !_registration.TryGetAction(unchecked((int)wParam), out var action)) { return 0; }

        handled = true;
        switch (action)
        {
            case OverlayHotkeyAction.ToggleDisplay:
                if (_overlay.CurrentWindow?.IsVisible == true) { _overlay.Hide(); }
                else { _overlay.Show(); }
                break;
            case OverlayHotkeyAction.Interactive:
                _overlay.ShowInteractive();
                break;
            case OverlayHotkeyAction.Hide:
                _overlay.Hide();
                break;
        }
        return 0;
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private static string Describe(OverlayHotkeyAction action) => action switch
    {
        OverlayHotkeyAction.ToggleDisplay => "show or hide the display",
        OverlayHotkeyAction.Interactive => "enter text mode",
        OverlayHotkeyAction.Hide => "hide all overlay windows",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

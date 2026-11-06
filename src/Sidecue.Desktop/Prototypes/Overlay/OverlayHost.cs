using System.Windows;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Owns one overlay instance at a time and reapplies policy after a real close.</summary>
public sealed class OverlayHost : IDisposable
{
    private readonly Window _owner;
    private readonly IWindowCaptureExclusion _exclusion;
    private bool _disposed;

    public OverlayHost(Window owner, IWindowCaptureExclusion exclusion)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exclusion);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _exclusion = exclusion;
        _owner.Closed += OnOwnerClosed;
    }

    public OverlayWindow? CurrentWindow { get; private set; }

    public OverlayWindow Show()
    {
        _owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentWindow is null)
        {
            CurrentWindow = new OverlayWindow(_owner, _exclusion);
            CurrentWindow.Closed += OnOverlayClosed;
        }
        CurrentWindow.Show();
        return CurrentWindow;
    }

    public void Hide()
    {
        _owner.Dispatcher.VerifyAccess();
        CurrentWindow?.Hide();
    }

    public void Close()
    {
        _owner.Dispatcher.VerifyAccess();
        CurrentWindow?.Close();
    }

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) { return; }
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        Close();
        GC.SuppressFinalize(this);
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow window)
        {
            window.Closed -= OnOverlayClosed;
            if (ReferenceEquals(CurrentWindow, window)) { CurrentWindow = null; }
        }
    }
}

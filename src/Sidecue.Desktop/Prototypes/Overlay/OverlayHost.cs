using System.Windows;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

/// <summary>Owns one overlay instance at a time and reapplies policy after a real close.</summary>
public sealed class OverlayHost : IDisposable
{
    private readonly Window _owner;
    private readonly IWindowCaptureExclusion _exclusion;
    private readonly IWindowActivationPolicy _activation;
    private string? _displayText;
    private bool _disposed;

    public OverlayHost(Window owner, IWindowCaptureExclusion exclusion, IWindowActivationPolicy? activation = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exclusion);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _exclusion = exclusion;
        _activation = activation ?? new WindowActivationPolicy();
        _owner.Closed += OnOwnerClosed;
    }

    public OverlayWindow? CurrentWindow { get; private set; }
    public OverlayDetailsWindow? DetailsWindow { get; private set; }
    public string StatusText { get; private set; } = "Overlay is hidden.";
    public event EventHandler? StatusChanged;

    public OverlayWindow Show()
    {
        var window = GetOrCreateWindow();
        window.ShowDisplay();
        SetStatus(window.ViewModel.ActivationStatusText);
        return window;
    }

    public OverlayWindow ShowInteractive()
    {
        var window = GetOrCreateWindow();
        window.ShowInteractive();
        SetStatus(window.ViewModel.ActivationStatusText);
        return window;
    }

    public void UpdateDisplay(string text)
    {
        _owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        _displayText = text;
        CurrentWindow?.ViewModel.SetDisplayText(text);
    }

    public OverlayDetailsWindow ShowDetails()
    {
        var overlay = GetOrCreateWindow();
        if (!overlay.IsVisible) { overlay.ShowDisplay(); }
        if (DetailsWindow is null)
        {
            DetailsWindow = new OverlayDetailsWindow(overlay, _exclusion, _activation);
            DetailsWindow.Closed += OnDetailsClosed;
            DetailsWindow.HideRequested += OnHideRequested;
        }
        // A failed parent focus policy must not expose a potentially activating surface.
        if (overlay.IsVisible)
        {
            DetailsWindow.ShowDisplay();
            SetStatus(DetailsWindow.ViewModel.ActivationStatusText);
        }
        else { SetStatus(overlay.ViewModel.ActivationStatusText); }
        return DetailsWindow;
    }

    public void Hide()
    {
        _owner.Dispatcher.VerifyAccess();
        DetailsWindow?.Hide();
        CurrentWindow?.Hide();
        SetStatus("All overlay windows are hidden.");
    }

    public void Close()
    {
        _owner.Dispatcher.VerifyAccess();
        DetailsWindow?.Close();
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
    private void OnHideRequested(object? sender, EventArgs e) => Hide();

    private OverlayWindow GetOrCreateWindow()
    {
        _owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (CurrentWindow is null)
        {
            CurrentWindow = new OverlayWindow(_owner, _exclusion, _activation);
            CurrentWindow.Closed += OnOverlayClosed;
            CurrentWindow.HideRequested += OnHideRequested;
            if (_displayText is not null) { CurrentWindow.ViewModel.SetDisplayText(_displayText); }
        }
        return CurrentWindow;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow window)
        {
            window.Closed -= OnOverlayClosed;
            window.HideRequested -= OnHideRequested;
            if (ReferenceEquals(CurrentWindow, window)) { CurrentWindow = null; }
            DetailsWindow?.Close();
        }
    }

    private void OnDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is OverlayDetailsWindow window)
        {
            window.Closed -= OnDetailsClosed;
            window.HideRequested -= OnHideRequested;
            if (ReferenceEquals(DetailsWindow, window)) { DetailsWindow = null; }
        }
    }
}

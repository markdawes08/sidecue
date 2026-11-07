namespace Sidecue.Windows.Windowing;

public enum OverlayHotkeyAction
{
    ToggleDisplay,
    Interactive,
    Hide,
}

public sealed record OverlayHotkeyBinding(int Id, OverlayHotkeyAction Action, string Shortcut, uint Modifiers, uint VirtualKey);
public sealed record OverlayHotkeyResult(OverlayHotkeyBinding Binding, bool Succeeded, int Win32Error);

public interface IHotkeyApi
{
    WindowOperationResult Register(nint owner, int id, uint modifiers, uint virtualKey);
    WindowOperationResult Unregister(nint owner, int id);
}

/// <summary>One fixed set per owner. Register and dispose on the HWND's thread.</summary>
public sealed class OverlayHotkeyRegistration : IDisposable
{
    private readonly IHotkeyApi _native;
    private nint _owner;
    private bool _attempted;
    private bool _disposed;

    // Application-owned IDs, below the shared-DLL range. MOD_NOREPEAT bounds key holding.
    public static IReadOnlyList<OverlayHotkeyBinding> Bindings { get; } = Array.AsReadOnly<OverlayHotkeyBinding>(
    [
        new(0x5301, OverlayHotkeyAction.ToggleDisplay, "Ctrl+Alt+Space", 0x4003, 0x20),
        new(0x5302, OverlayHotkeyAction.Interactive, "Ctrl+Alt+Enter", 0x4003, 0x0D),
        new(0x5303, OverlayHotkeyAction.Hide, "Ctrl+Alt+Escape", 0x4003, 0x1B),
    ]);

    public IReadOnlyList<OverlayHotkeyResult> Results { get; private set; } = Array.Empty<OverlayHotkeyResult>();
    public IReadOnlyList<OverlayHotkeyResult> ReleaseResults { get; private set; } = Array.Empty<OverlayHotkeyResult>();

    public OverlayHotkeyRegistration() : this(new Win32HotkeyApi()) { }

    public OverlayHotkeyRegistration(IHotkeyApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public IReadOnlyList<OverlayHotkeyResult> Register(nint owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attempted)
        {
            throw new InvalidOperationException("This hotkey set has already attempted registration.");
        }

        if (owner == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(owner));
        }

        _owner = owner;
        _attempted = true;
        var results = new List<OverlayHotkeyResult>(Bindings.Count);
        Results = results.AsReadOnly();
        foreach (var binding in Bindings)
        {
            var result = _native.Register(owner, binding.Id, binding.Modifiers, binding.VirtualKey);
            results.Add(new(binding, result.Succeeded, result.Succeeded ? 0 : result.Win32Error));
        }

        return Results;
    }

    public bool TryGetAction(int id, out OverlayHotkeyAction action)
    {
        if (!_disposed)
        {
            foreach (var result in Results)
            {
                if (result.Succeeded && result.Binding.Id == id)
                {
                    action = result.Binding.Action;
                    return true;
                }
            }
        }

        action = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var releases = new List<OverlayHotkeyResult>(Bindings.Count);
        foreach (var registration in Results)
        {
            if (registration.Succeeded)
            {
                var result = _native.Unregister(_owner, registration.Binding.Id);
                releases.Add(new(registration.Binding, result.Succeeded, result.Succeeded ? 0 : result.Win32Error));
            }
        }

        ReleaseResults = releases.AsReadOnly();
        GC.SuppressFinalize(this);
    }
}

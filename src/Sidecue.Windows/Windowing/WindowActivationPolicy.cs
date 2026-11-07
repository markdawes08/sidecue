namespace Sidecue.Windows.Windowing;

public readonly record struct WindowOperationResult(bool Succeeded, int Win32Error);
public readonly record struct WindowStyleReadResult(bool Succeeded, nint Style, int Win32Error);

public interface IWindowActivationPolicy
{
    WindowOperationResult Apply(nint window, bool interactive);
}

public interface IWindowStyleApi
{
    WindowStyleReadResult ReadExtendedStyle(nint window);
    WindowOperationResult WriteExtendedStyle(nint window, nint style);
    WindowOperationResult RefreshWithoutActivation(nint window);
}

/// <summary>Changes activation eligibility without requesting foreground focus.</summary>
public sealed class WindowActivationPolicy : IWindowActivationPolicy
{
    public const int NoActivateStyle = 0x08000000;
    private readonly IWindowStyleApi _native;

    public WindowActivationPolicy() : this(new Win32WindowStyleApi()) { }

    public WindowActivationPolicy(IWindowStyleApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public WindowOperationResult Apply(nint window, bool interactive)
    {
        if (window == 0)
        {
            return new(false, 1400); // ERROR_INVALID_WINDOW_HANDLE
        }

        var current = _native.ReadExtendedStyle(window);
        if (!current.Succeeded)
        {
            return new(false, current.Win32Error);
        }

        var desired = interactive
            ? current.Style & ~(nint)NoActivateStyle
            : current.Style | NoActivateStyle;
        var written = _native.WriteExtendedStyle(window, desired);
        if (!written.Succeeded)
        {
            return written;
        }

        // Refresh cached styles even if the value already matched: a previous
        // attempt may have written the style but failed the frame refresh.
        return _native.RefreshWithoutActivation(window);
    }
}

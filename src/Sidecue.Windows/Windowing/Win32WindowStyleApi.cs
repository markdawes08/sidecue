using System.Runtime.InteropServices;

namespace Sidecue.Windows.Windowing;

public sealed class Win32WindowStyleApi : IWindowStyleApi
{
    private const int ExtendedStyleIndex = -20;
    private const uint RefreshFlags = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 | 0x0200;

    public WindowStyleReadResult ReadExtendedStyle(nint window)
    {
        // A zero style is valid. .NET clears the native last error before a
        // SetLastError=true P/Invoke; inspect it only for an ambiguous zero.
        var style = NativeMethods.GetWindowLongPtrW(window, ExtendedStyleIndex);
        var error = style == 0 ? Marshal.GetLastWin32Error() : 0;
        return new(error == 0, style, error);
    }

    public WindowOperationResult WriteExtendedStyle(nint window, nint style)
    {
        var previous = NativeMethods.SetWindowLongPtrW(window, ExtendedStyleIndex, style);
        var error = previous == 0 ? Marshal.GetLastWin32Error() : 0;
        return new(error == 0, error);
    }

    public WindowOperationResult RefreshWithoutActivation(nint window)
    {
        // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED | NOOWNERZORDER.
        var succeeded = NativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, RefreshFlags) != 0;
        return new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private static class NativeMethods
    {
        // Sidecue targets x64. The Ptr exports are the native 64-bit entry points.
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint GetWindowLongPtrW(nint window, int index);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}

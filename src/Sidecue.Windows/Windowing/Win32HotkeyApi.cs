using System.Runtime.InteropServices;

namespace Sidecue.Windows.Windowing;

public sealed class Win32HotkeyApi : IHotkeyApi
{
    /// <summary>Exercises a local owner's message route; this does not simulate physical key input.</summary>
    public static void SendDiagnosticNotification(nint owner, int id) =>
        NativeMethods.SendMessageW(owner, 0x0312, id, 0);

    public WindowOperationResult Register(nint owner, int id, uint modifiers, uint virtualKey)
    {
        var succeeded = NativeMethods.RegisterHotKey(owner, id, modifiers, virtualKey) != 0;
        return new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    public WindowOperationResult Unregister(nint owner, int id)
    {
        var succeeded = NativeMethods.UnregisterHotKey(owner, id) != 0;
        return new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int UnregisterHotKey(nint window, int id);
    }
}

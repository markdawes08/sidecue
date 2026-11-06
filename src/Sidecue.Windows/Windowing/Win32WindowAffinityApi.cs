using System.Runtime.InteropServices;

namespace Sidecue.Windows.Windowing;

public readonly record struct CompositionStatus(bool Enabled, int HResult);
public readonly record struct WindowAffinityCallResult(bool Succeeded, int Win32Error);
public readonly record struct WindowAffinityReadResult(bool Succeeded, uint Affinity, int Win32Error);

/// <summary>The small native boundary used by capability policy and deterministic failure tests.</summary>
public interface IWindowAffinityApi
{
    bool SupportsExcludeFromCapture { get; }
    CompositionStatus GetCompositionStatus();
    WindowAffinityCallResult SetDisplayAffinity(nint window, uint affinity);
}

public sealed class Win32WindowAffinityApi : IWindowAffinityApi
{
    public bool SupportsExcludeFromCapture => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public CompositionStatus GetCompositionStatus()
    {
        var result = NativeMethods.DwmIsCompositionEnabled(out var enabled);
        return new(enabled != 0, result);
    }

    public WindowAffinityCallResult SetDisplayAffinity(nint window, uint affinity)
    {
        var succeeded = NativeMethods.SetWindowDisplayAffinity(window, affinity) != 0;
        return new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    /// <summary>Diagnostic readback only; even successful readback is not capture evidence.</summary>
    /// <remarks>GetWindowDisplayAffinity has DWM/window-style requirements. Its failure does not
    /// retroactively change the separate SetWindowDisplayAffinity return value.</remarks>
    public static WindowAffinityReadResult ReadDisplayAffinity(nint window)
    {
        var succeeded = NativeMethods.GetWindowDisplayAffinity(window, out var affinity) != 0;
        return new(succeeded, affinity, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private static class NativeMethods
    {
        // Win32 BOOL is a four-byte integer. These signatures are blittable and
        // keep HWNDs confined to the Windows adapter; the caller owns each HWND.
        [DllImport("dwmapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int DwmIsCompositionEnabled(out int enabled);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int SetWindowDisplayAffinity(nint window, uint affinity);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetWindowDisplayAffinity(nint window, out uint affinity);
    }
}

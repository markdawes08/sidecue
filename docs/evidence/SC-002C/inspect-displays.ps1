$source = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class SidecueDisplayInspection {
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref RECT bounds, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFOEX {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static string[] Inspect() {
        var lines = new List<string>();
        bool aware = SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        lines.Add("PerMonitorV2=" + aware);
        int count = 0;
        bool enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr handle, IntPtr dc, ref RECT rect, IntPtr data) {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            bool infoOk = GetMonitorInfo(handle, ref info);
            uint x, y; int hr = GetDpiForMonitor(handle, 0, out x, out y); // MDT_EFFECTIVE_DPI
            count++;
            lines.Add(String.Format("Monitor={0}; Bounds={1},{2}..{3},{4}; Primary={5}; Dpi={6}x{7}; Scale={8}%; InfoOk={9}; DpiHResult={10}",
                info.szDevice, rect.left, rect.top, rect.right, rect.bottom, (info.dwFlags & 1) != 0, x, y, x * 100 / 96, infoOk, hr));
            return true;
        }, IntPtr.Zero);
        lines.Add("EnumerationSucceeded=" + enumerated + "; ActiveMonitorCount=" + count);
        lines.Add("ForegroundWindowPresent=" + (GetForegroundWindow() != IntPtr.Zero));
        return lines.ToArray();
    }
}
'@
Add-Type -TypeDefinition $source
Write-Output ('Timestamp=' + (Get-Date -Format o))
Write-Output ('UserInteractive=' + [Environment]::UserInteractive + '; Session=' + $env:SESSIONNAME)
[SidecueDisplayInspection]::Inspect()
Get-CimInstance Win32_Keyboard | Select-Object Name, Status | Format-Table -AutoSize
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object DisplayVersion, CurrentBuild, UBR | Format-List
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion | Format-Table -AutoSize

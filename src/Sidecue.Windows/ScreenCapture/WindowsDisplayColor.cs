using System.Runtime.InteropServices;

namespace Sidecue.Windows.ScreenCapture;

internal static class WindowsDisplayColor
{
    private const uint ActivePaths = 0x00000002;
    private const uint VirtualModeAware = 0x00000010;
    private const uint QueryFlags = ActivePaths | VirtualModeAware;
    private const uint PathActive = 0x00000001;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GetAdvancedColorInfo = 9;
    private const uint GetAdvancedColorInfo2 = 15;
    private const uint AdvancedColorActive = 1u << 1;
    private const uint LegacyWideColorEnforced = 1u << 2;
    private const uint HdrUserEnabled = 1u << 5;
    private const uint WideColorUserEnabled = 1u << 7;
    private const int QueryAttempts = 3;

    internal static ScreenshotColorMode GetActiveMode()
    {
        // The topology can change between sizing the buffers and querying them.
        for (int attempt = 0; attempt < QueryAttempts; attempt++)
        {
            int result = GetDisplayConfigBufferSizes(QueryFlags, out uint pathCount, out uint modeCount);
            if (result != 0 || pathCount == 0 || modeCount == 0)
            {
                return ScreenshotColorMode.Unknown;
            }

            var paths = new DisplayPathInfo[pathCount];
            var modes = new DisplayModeInfo[modeCount];
            result = QueryDisplayConfig(QueryFlags, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }
            if (result != 0 || pathCount == 0 || pathCount > paths.Length || modeCount > modes.Length)
            {
                return ScreenshotColorMode.Unknown;
            }

            // One known Advanced Color output is enough to reject capture, even
            // if another target could not report its active mode.
            ScreenshotColorMode combined = ScreenshotColorMode.Sdr;
            for (int index = 0; index < pathCount; index++)
            {
                ref readonly DisplayPathInfo path = ref paths[index];
                ScreenshotColorMode mode = (path.Flags & PathActive) == 0 || path.TargetAvailable == 0
                    ? ScreenshotColorMode.Unknown
                    : GetTargetMode(path.AdapterId, path.TargetId);
                if (mode == ScreenshotColorMode.AdvancedColor)
                {
                    return mode;
                }
                if (mode == ScreenshotColorMode.Unknown)
                {
                    combined = mode;
                }
            }
            return combined;
        }
        return ScreenshotColorMode.Unknown;
    }

    private static ScreenshotColorMode GetTargetMode(AdapterLuid adapterId, uint targetId)
    {
        var color = new DisplayColorInfo2
        {
            Header = new(GetAdvancedColorInfo2, (uint)Marshal.SizeOf<DisplayColorInfo2>(), adapterId, targetId)
        };
        int result = DisplayConfigGetDeviceInfo(ref color);
        if (result == 0)
        {
            if ((color.Flags & (AdvancedColorActive | HdrUserEnabled | WideColorUserEnabled)) != 0)
            {
                return ScreenshotColorMode.AdvancedColor;
            }
            return color.ActiveMode switch
            {
                ActiveColorMode.Sdr => ScreenshotColorMode.Sdr,
                ActiveColorMode.WideColor or ActiveColorMode.Hdr => ScreenshotColorMode.AdvancedColor,
                _ => ScreenshotColorMode.Unknown
            };
        }
        if (result is not (ErrorNotSupported or ErrorInvalidParameter))
        {
            return ScreenshotColorMode.Unknown;
        }

        // Some drivers only expose the older advanced-color enable flags.
        var legacy = new DisplayColorInfo
        {
            Header = new(GetAdvancedColorInfo, (uint)Marshal.SizeOf<DisplayColorInfo>(), adapterId, targetId)
        };
        return DisplayConfigGetDeviceInfo(ref legacy) switch
        {
            0 when (legacy.Flags & (AdvancedColorActive | LegacyWideColorEnforced)) != 0 => ScreenshotColorMode.AdvancedColor,
            0 => ScreenshotColorMode.Sdr,
            _ => ScreenshotColorMode.Unknown
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    // DISPLAYCONFIG_PATH_INFO contains a 20-byte source followed by a 48-byte
    // target and path flags. Only the active target identity and state are read.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct DisplayPathInfo
    {
        [FieldOffset(20)] internal AdapterLuid AdapterId;
        [FieldOffset(28)] internal uint TargetId;
        [FieldOffset(60)] internal int TargetAvailable;
        [FieldOffset(68)] internal uint Flags;
    }

    // QueryDisplayConfig requires the DISPLAYCONFIG_MODE_INFO output buffer.
    // Its union is 48 bytes, aligned to eight bytes on the supported x64 ABI.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayModeInfo
    {
        [FieldOffset(16)] internal ulong ModeUnionAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DisplayDeviceInfoHeader(uint type, uint size, AdapterLuid adapterId, uint id)
    {
        private readonly uint _type = type;
        private readonly uint _size = size;
        private readonly AdapterLuid _adapterId = adapterId;
        private readonly uint _id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayColorInfo
    {
        internal DisplayDeviceInfoHeader Header;
        internal uint Flags;
        internal uint ColorEncoding;
        internal uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayColorInfo2
    {
        internal DisplayDeviceInfoHeader Header;
        internal uint Flags;
        internal uint ColorEncoding;
        internal uint BitsPerColorChannel;
        internal ActiveColorMode ActiveMode;
    }

    private enum ActiveColorMode : uint
    {
        Sdr,
        WideColor,
        Hdr
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPathInfo[] paths,
        ref uint modeCount, [Out] DisplayModeInfo[] modes, nint topologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayColorInfo request);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayColorInfo2 request);
}

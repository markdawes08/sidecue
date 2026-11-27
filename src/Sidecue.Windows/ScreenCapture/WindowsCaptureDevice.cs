using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Sidecue.Windows.ScreenCapture;

internal static class WindowsCaptureDevice
{
    private const uint HardwareDriver = 1;
    private const uint BgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const int DxgiUnsupported = unchecked((int)0x887A0004);
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    internal static IDirect3DDevice Create()
    {
        nint nativeDevice = 0;
        nint dxgiDevice = 0;
        nint graphicsDevice = 0;
        try
        {
            int result = D3D11CreateDevice(0, HardwareDriver, 0, BgraSupport, 0, 0, D3d11SdkVersion, out nativeDevice, 0, 0);
            if (result == DxgiUnsupported)
            {
                throw new ScreenshotCaptureException(ScreenshotCaptureFailure.Unsupported);
            }
            Marshal.ThrowExceptionForHR(result, new nint(-1));

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in DxgiDeviceId, out dxgiDevice), new nint(-1));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice), new nint(-1));

            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice) ??
                throw new ScreenshotCaptureException(ScreenshotCaptureFailure.CaptureFailed);
        }
        finally
        {
            // The projected WinRT object has its own reference after FromAbi.
            if (graphicsDevice != 0) Marshal.Release(graphicsDevice);
            if (dxgiDevice != 0) Marshal.Release(dxgiDevice);
            if (nativeDevice != 0) Marshal.Release(nativeDevice);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D11CreateDevice(
        nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount,
        uint sdkVersion, out nint device, nint selectedFeatureLevel, nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

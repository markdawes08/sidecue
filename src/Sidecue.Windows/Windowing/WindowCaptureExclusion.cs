namespace Sidecue.Windows.Windowing;

public enum CaptureExclusionStatus
{
    NotApplied,
    ApiAccepted,
    Unavailable,
    Failed
}

public enum CaptureExclusionReason
{
    None,
    InvalidWindow,
    UnsupportedWindows,
    CompositionDisabled,
    CompositionQueryFailed,
    NativeCallFailed
}

/// <summary>An API capability result, never evidence of receiving-side capture exclusion.</summary>
public sealed record CaptureExclusionResult
{
    public CaptureExclusionResult(CaptureExclusionStatus status, CaptureExclusionReason reason,
        int? win32Error = null, int? hResult = null)
    {
        if (!Enum.IsDefined(status)) { throw new ArgumentOutOfRangeException(nameof(status)); }
        if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
        if (win32Error is not null && hResult is not null)
        {
            throw new ArgumentException("A result cannot combine Win32 and HRESULT error codes.", nameof(hResult));
        }
        Status = status;
        Reason = reason;
        Win32Error = win32Error;
        HResult = hResult;
    }

    public CaptureExclusionStatus Status { get; }
    public CaptureExclusionReason Reason { get; }
    public int? Win32Error { get; }
    public int? HResult { get; }
}

public interface IWindowCaptureExclusion
{
    /// <summary>Request exclusion for a ready top-level HWND owned by this process.</summary>
    CaptureExclusionResult Apply(nint window);
}

/// <summary>Applies the exact exclusion policy without claiming universal capture protection.</summary>
public sealed class WindowCaptureExclusion : IWindowCaptureExclusion
{
    public const uint ExcludeFromCapture = 0x00000011;
    private readonly IWindowAffinityApi _native;

    public WindowCaptureExclusion() : this(new Win32WindowAffinityApi()) { }

    public WindowCaptureExclusion(IWindowAffinityApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public CaptureExclusionResult Apply(nint window)
    {
        if (window == 0)
        {
            return new(CaptureExclusionStatus.Failed, CaptureExclusionReason.InvalidWindow);
        }
        if (!_native.SupportsExcludeFromCapture)
        {
            // Earlier Windows versions may treat 0x11 as WDA_MONITOR. Never call
            // that degraded behavior an accepted exclusion capability.
            return new(CaptureExclusionStatus.Unavailable, CaptureExclusionReason.UnsupportedWindows);
        }
        var composition = _native.GetCompositionStatus();
        if (composition.HResult < 0)
        {
            return new(CaptureExclusionStatus.Failed, CaptureExclusionReason.CompositionQueryFailed,
                hResult: composition.HResult);
        }
        if (!composition.Enabled)
        {
            return new(CaptureExclusionStatus.Unavailable, CaptureExclusionReason.CompositionDisabled);
        }
        var applied = _native.SetDisplayAffinity(window, ExcludeFromCapture);
        return applied.Succeeded
            ? new(CaptureExclusionStatus.ApiAccepted, CaptureExclusionReason.None)
            : new(CaptureExclusionStatus.Failed, CaptureExclusionReason.NativeCallFailed,
                win32Error: applied.Win32Error);
    }
}

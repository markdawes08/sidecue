using System.ComponentModel;
using System.Globalization;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private CaptureExclusionResult _result = new(CaptureExclusionStatus.NotApplied, CaptureExclusionReason.None);

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureExclusionResult Result => _result;

    public string StatusText => Result.Status switch
    {
        CaptureExclusionStatus.ApiAccepted => "Windows accepted the capture-exclusion request.",
        CaptureExclusionStatus.Unavailable => "Capture exclusion is unavailable.",
        CaptureExclusionStatus.Failed => "Windows did not accept the capture-exclusion request.",
        _ => "Capture exclusion has not been requested yet."
    };

    public string DetailText
    {
        get
        {
            var detail = Result.Reason switch
            {
                CaptureExclusionReason.InvalidWindow => "The overlay window is not ready.",
                CaptureExclusionReason.UnsupportedWindows => "This Windows version does not support capture exclusion.",
                CaptureExclusionReason.CompositionDisabled => "Windows desktop composition is unavailable.",
                CaptureExclusionReason.CompositionQueryFailed => "Windows could not report desktop composition availability.",
                CaptureExclusionReason.NativeCallFailed => "You can hide this overlay immediately.",
                _ => Result.Status == CaptureExclusionStatus.ApiAccepted
                    ? "This reports the Windows API result only."
                    : "The request will be applied when this window is ready."
            };
            if (Result.Win32Error is { } error)
            {
                detail += string.Create(CultureInfo.InvariantCulture, $" Windows error: {error}.");
            }
            if (Result.HResult is { } hresult)
            {
                detail += string.Create(CultureInfo.InvariantCulture, $" HRESULT: 0x{hresult:X8}.");
            }
            return detail;
        }
    }

    public string VerificationNotice { get; } = "Screen-sharing behavior has not been verified.";

    internal void SetResult(CaptureExclusionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
    }
}

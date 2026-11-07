using System.ComponentModel;
using System.Globalization;
using Sidecue.Windows.Windowing;

namespace Sidecue.Desktop.Prototypes.Overlay;

public enum OverlayMode { Display, Interactive }

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private CaptureExclusionResult _result = new(CaptureExclusionStatus.NotApplied, CaptureExclusionReason.None);
    private OverlayMode _mode;
    private string _displayText = "Synthetic display content. No meeting or provider is connected.";
    private string _activationStatusText = "Display mode does not request keyboard focus.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaptureExclusionResult Result => _result;
    public OverlayMode Mode => _mode;
    public bool IsInteractive => Mode == OverlayMode.Interactive;
    public string ModeText => IsInteractive ? "Input mode" : "Display mode";
    public string DisplayText => _displayText;
    public string ActivationStatusText => _activationStatusText;
    public WindowOperationResult? ActivationResult { get; private set; }

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

    internal void SetDisplayText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _displayText = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
    }

    internal void SetActivationResult(OverlayMode requestedMode, WindowOperationResult result)
    {
        ActivationResult = result;
        _mode = result.Succeeded ? requestedMode : OverlayMode.Display;
        _activationStatusText = result.Succeeded
            ? IsInteractive ? "Local input is enabled. Nothing is sent or saved." : "Display mode does not request keyboard focus."
            : string.Create(CultureInfo.InvariantCulture, $"Windows could not apply the focus mode (error {result.Win32Error}). Input is disabled; the window is hidden.");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivationResult)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInteractive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivationStatusText)));
    }

    internal void ReportActivationDenied()
    {
        _activationStatusText = "Windows did not give this window keyboard focus. Select the input box to type.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivationStatusText)));
    }

    internal void SetResult(CaptureExclusionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailText)));
    }
}

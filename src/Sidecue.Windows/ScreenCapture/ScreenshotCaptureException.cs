namespace Sidecue.Windows.ScreenCapture;

public enum ScreenshotCaptureFailure
{
    Unsupported,
    TargetClosed,
    SizeChanged,
    ImageTooLarge,
    TimedOut,
    CaptureFailed,
    DeviceLost,
    UnsupportedFormat,
    AdvancedColor,
    DisplayColorUnknown
}

/// <summary>A capture failure with content-free text suitable for a local status message.</summary>
public sealed class ScreenshotCaptureException(ScreenshotCaptureFailure reason) : Exception(reason switch
{
    ScreenshotCaptureFailure.Unsupported => "Windows screenshot capture is unavailable.",
    ScreenshotCaptureFailure.TargetClosed => "The selected target closed. Choose a target again.",
    ScreenshotCaptureFailure.SizeChanged => "The selected target changed size. Choose a target again.",
    ScreenshotCaptureFailure.ImageTooLarge => "The selected target exceeds the screenshot size limit.",
    ScreenshotCaptureFailure.TimedOut => "The selected target did not provide a frame in time.",
    ScreenshotCaptureFailure.DeviceLost => "The graphics device stopped responding. Choose a screenshot target again.",
    ScreenshotCaptureFailure.UnsupportedFormat => "The selected target returned an unsupported pixel format.",
    ScreenshotCaptureFailure.AdvancedColor => "Screenshot capture requires SDR on all active displays. HDR or Advanced Color is enabled.",
    ScreenshotCaptureFailure.DisplayColorUnknown => "Windows could not verify the SDR display mode. Choose a screenshot target again.",
    _ => "Windows could not capture the selected target. Choose a target again."
})
{
    public ScreenshotCaptureFailure Reason { get; } = reason;
}

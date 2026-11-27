namespace Sidecue.Windows.ScreenCapture;

public enum ScreenshotCaptureFailure
{
    Unsupported,
    TargetClosed,
    SizeChanged,
    ImageTooLarge,
    TimedOut,
    CaptureFailed
}

/// <summary>A capture failure with content-free text suitable for a local status message.</summary>
public sealed class ScreenshotCaptureException(ScreenshotCaptureFailure reason) : Exception(reason switch
{
    ScreenshotCaptureFailure.Unsupported => "Windows screenshot capture is unavailable.",
    ScreenshotCaptureFailure.TargetClosed => "The selected target closed. Choose a target again.",
    ScreenshotCaptureFailure.SizeChanged => "The selected target changed size. Choose a target again.",
    ScreenshotCaptureFailure.ImageTooLarge => "The selected target exceeds the screenshot size limit.",
    ScreenshotCaptureFailure.TimedOut => "The selected target did not provide a frame in time.",
    _ => "Windows could not capture the selected target. Choose a target again."
})
{
    public ScreenshotCaptureFailure Reason { get; } = reason;
}

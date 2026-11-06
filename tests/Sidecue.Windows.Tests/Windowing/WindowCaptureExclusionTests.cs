using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Windows.Tests.Windowing;

public sealed class WindowCaptureExclusionTests
{
    [Fact]
    public void MissingHandleFailsBeforeAnyNativeCall()
    {
        var native = new FakeNative();

        var result = new WindowCaptureExclusion(native).Apply(0);

        Assert.Equal(CaptureExclusionStatus.Failed, result.Status);
        Assert.Equal(CaptureExclusionReason.InvalidWindow, result.Reason);
        Assert.Equal(0, native.CompositionCalls);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public void UnsupportedWindowsDoesNotSilentlyFallBackToMonitorAffinity()
    {
        var native = new FakeNative { SupportsExcludeFromCapture = false };

        var result = new WindowCaptureExclusion(native).Apply(123);

        Assert.Equal(CaptureExclusionStatus.Unavailable, result.Status);
        Assert.Equal(CaptureExclusionReason.UnsupportedWindows, result.Reason);
        Assert.Equal(0, native.CompositionCalls);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public void DisabledCompositionIsUnavailableWithoutSettingAffinity()
    {
        var native = new FakeNative { Composition = new(false, 0) };

        var result = new WindowCaptureExclusion(native).Apply(123);

        Assert.Equal(CaptureExclusionStatus.Unavailable, result.Status);
        Assert.Equal(CaptureExclusionReason.CompositionDisabled, result.Reason);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public void CompositionQueryFailurePreservesItsHResult()
    {
        const int failure = unchecked((int)0x80004005);
        var native = new FakeNative { Composition = new(false, failure) };

        var result = new WindowCaptureExclusion(native).Apply(123);

        Assert.Equal(CaptureExclusionStatus.Failed, result.Status);
        Assert.Equal(CaptureExclusionReason.CompositionQueryFailed, result.Reason);
        Assert.Equal(failure, result.HResult);
        Assert.Null(result.Win32Error);
        Assert.Equal(0, native.SetCalls);
    }

    [Fact]
    public void NativeFailurePreservesWin32ErrorWithoutRetryingAnotherAffinity()
    {
        var native = new FakeNative { SetResult = new(false, 5) };

        var result = new WindowCaptureExclusion(native).Apply(123);

        Assert.Equal(CaptureExclusionStatus.Failed, result.Status);
        Assert.Equal(CaptureExclusionReason.NativeCallFailed, result.Reason);
        Assert.Equal(5, result.Win32Error);
        Assert.Null(result.HResult);
        Assert.Equal(1, native.SetCalls);
        Assert.Equal(0x11U, native.LastAffinity);
    }

    [Fact]
    public void SuccessReportsOnlyApiAcceptanceForTheExactHandleAndFlag()
    {
        // A stale error value must not be presented when the native call succeeded.
        var native = new FakeNative { SetResult = new(true, 5) };

        var result = new WindowCaptureExclusion(native).Apply(456);

        Assert.Equal(CaptureExclusionStatus.ApiAccepted, result.Status);
        Assert.Equal(CaptureExclusionReason.None, result.Reason);
        Assert.Null(result.Win32Error);
        Assert.Null(result.HResult);
        Assert.Equal((nint)456, native.LastWindow);
        Assert.Equal(0x11U, native.LastAffinity);
        Assert.Equal(1, native.SetCalls);
    }

    private sealed class FakeNative : IWindowAffinityApi
    {
        public bool SupportsExcludeFromCapture { get; init; } = true;
        public CompositionStatus Composition { get; init; } = new(true, 0);
        public WindowAffinityCallResult SetResult { get; init; } = new(true, 0);
        public int CompositionCalls { get; private set; }
        public int SetCalls { get; private set; }
        public nint LastWindow { get; private set; }
        public uint LastAffinity { get; private set; }

        public CompositionStatus GetCompositionStatus()
        {
            CompositionCalls++;
            return Composition;
        }

        public WindowAffinityCallResult SetDisplayAffinity(nint window, uint affinity)
        {
            SetCalls++;
            LastWindow = window;
            LastAffinity = affinity;
            return SetResult;
        }
    }
}

using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Windows.Tests.Windowing;

public sealed class WindowActivationPolicyTests
{
    [Fact]
    public void DisplayAndInteractivePreserveOtherStylesAndRefreshWithoutActivation()
    {
        const int existingStyles = 0x00000108;
        var native = new FakeStyleApi { Style = existingStyles };
        var policy = new WindowActivationPolicy(native);

        Assert.True(policy.Apply(123, interactive: false).Succeeded);
        Assert.Equal((nint)(existingStyles | WindowActivationPolicy.NoActivateStyle), native.Style);
        Assert.True(policy.Apply(123, interactive: true).Succeeded);
        Assert.Equal((nint)existingStyles, native.Style);
        Assert.Equal(2, native.RefreshCalls);
        Assert.Equal((nint)123, native.LastWindow);
    }

    [Fact]
    public void MissingHandleDoesNotCallNativeApi()
    {
        var native = new FakeStyleApi();

        Assert.Equal(new WindowOperationResult(false, 1400), new WindowActivationPolicy(native).Apply(0, false));
        Assert.Equal(0, native.ReadCalls);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("refresh")]
    public void FailuresRemainVisibleAndStopDependentOperations(string failure)
    {
        var native = new FakeStyleApi { Failure = failure };

        var result = new WindowActivationPolicy(native).Apply(123, false);

        Assert.Equal(new WindowOperationResult(false, 5), result);
        Assert.Equal(failure == "read" ? 0 : 1, native.WriteCalls);
        Assert.Equal(failure == "refresh" ? 1 : 0, native.RefreshCalls);
    }

    private sealed class FakeStyleApi : IWindowStyleApi
    {
        public nint Style { get; set; }
        public string? Failure { get; init; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public nint LastWindow { get; private set; }

        public WindowStyleReadResult ReadExtendedStyle(nint window)
        {
            LastWindow = window;
            ReadCalls++;
            return new(Failure != "read", Style, Failure == "read" ? 5 : 0);
        }

        public WindowOperationResult WriteExtendedStyle(nint window, nint style)
        {
            LastWindow = window;
            WriteCalls++;
            if (Failure == "write")
            {
                return new(false, 5);
            }

            Style = style;
            return new(true, 0);
        }

        public WindowOperationResult RefreshWithoutActivation(nint window)
        {
            LastWindow = window;
            RefreshCalls++;
            return new(Failure != "refresh", Failure == "refresh" ? 5 : 0);
        }
    }
}

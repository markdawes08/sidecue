using Sidecue.Windows.Windowing;
using Xunit;

namespace Sidecue.Windows.Tests.Windowing;

public sealed class OverlayHotkeyRegistrationTests
{
    [Fact]
    public void FixedSetSuppressesRepeatAndRegistersOnlyOnce()
    {
        var native = new FakeHotkeyApi();
        using var registration = new OverlayHotkeyRegistration(native);

        var results = registration.Register(123);

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(new uint[] { 0x20, 0x0D, 0x1B }, native.Calls.Select(call => call.Key));
        Assert.All(native.Calls, call => Assert.Equal(0x4003U, call.Modifiers));
        Assert.All(native.Calls, call => Assert.Equal((nint)123, call.Owner));
        Assert.Throws<InvalidOperationException>(() => registration.Register(123));
        Assert.Equal(3, native.Calls.Count);
        Assert.True(registration.TryGetAction(results[1].Binding.Id, out var action));
        Assert.Equal(OverlayHotkeyAction.Interactive, action);
        Assert.False(registration.TryGetAction(-1, out _));
    }

    [Fact]
    public void ConflictIsReportedAndOnlySuccessfulRegistrationsAreReleased()
    {
        var conflictId = OverlayHotkeyRegistration.Bindings[1].Id;
        var native = new FakeHotkeyApi { ConflictId = conflictId };
        var registration = new OverlayHotkeyRegistration(native);
        var results = registration.Register(456);

        Assert.False(results[1].Succeeded);
        Assert.Equal(1409, results[1].Win32Error);
        Assert.False(registration.TryGetAction(conflictId, out _));

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(results.Where(result => result.Succeeded).Select(result => result.Binding.Id), native.ReleasedIds);
        Assert.All(registration.ReleaseResults, result => Assert.True(result.Succeeded));
        Assert.False(registration.TryGetAction(results[0].Binding.Id, out _));
        Assert.Throws<ObjectDisposedException>(() => registration.Register(456));
    }

    [Fact]
    public void InvalidOwnerAndUnusedDisposalMakeNoNativeCalls()
    {
        var native = new FakeHotkeyApi();
        var registration = new OverlayHotkeyRegistration(native);

        Assert.Throws<ArgumentException>(() => registration.Register(0));
        registration.Dispose();

        Assert.Empty(native.Calls);
        Assert.Empty(native.ReleasedIds);
    }

    [Fact]
    public void ReleaseFailuresAreRetainedWithoutRoutingStaleMessages()
    {
        var native = new FakeHotkeyApi { ReleaseResult = new(false, 5) };
        var registration = new OverlayHotkeyRegistration(native);
        registration.Register(123);

        registration.Dispose();

        Assert.Equal(3, native.ReleasedIds.Count);
        Assert.All(registration.ReleaseResults, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Equal(5, result.Win32Error);
            Assert.False(registration.TryGetAction(result.Binding.Id, out _));
        });
    }

    private sealed class FakeHotkeyApi : IHotkeyApi
    {
        public int? ConflictId { get; init; }
        public WindowOperationResult ReleaseResult { get; init; } = new(true, 0);
        public List<(nint Owner, int Id, uint Modifiers, uint Key)> Calls { get; } = [];
        public List<int> ReleasedIds { get; } = [];

        public WindowOperationResult Register(nint owner, int id, uint modifiers, uint virtualKey)
        {
            Calls.Add((owner, id, modifiers, virtualKey));
            return new(id != ConflictId, id == ConflictId ? 1409 : 0);
        }

        public WindowOperationResult Unregister(nint owner, int id)
        {
            Assert.Equal(Calls[0].Owner, owner);
            ReleasedIds.Add(id);
            return ReleaseResult;
        }
    }
}

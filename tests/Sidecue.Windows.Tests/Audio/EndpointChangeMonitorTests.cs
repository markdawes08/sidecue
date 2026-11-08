using NAudio.CoreAudioApi;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;

namespace Sidecue.Windows.Tests.Audio;

public sealed class EndpointChangeMonitorTests
{
    [Fact]
    public void OnlyMultimediaCaptureAndRenderDefaultChangesAreReported()
    {
        var changes = new List<CaptureSourceKind>();
        var router = new EndpointChangeRouter(changes.Add);

        router.DefaultChanged(DataFlow.Capture, Role.Console, "unrelated");
        router.DefaultChanged(DataFlow.Render, Role.Communications, "unrelated");
        router.DefaultChanged(DataFlow.All, Role.Multimedia, "unrelated");
        router.DefaultChanged(DataFlow.Capture, Role.Multimedia, "capture");
        router.DefaultChanged(DataFlow.Render, Role.Multimedia, null);

        Assert.Equal([CaptureSourceKind.Microphone, CaptureSourceKind.RemotePlayback], changes);
    }

    [Fact]
    public void OnlySelectedInactiveOrRemovedEndpointsTriggerRecovery()
    {
        var changes = new List<CaptureSourceKind>();
        var router = new EndpointChangeRouter(changes.Add);
        router.Initialize(DataFlow.Capture, router.SnapshotToken(DataFlow.Capture), "capture");
        router.Initialize(DataFlow.Render, router.SnapshotToken(DataFlow.Render), "render");

        router.StateChanged("other", DeviceState.Unplugged);
        router.StateChanged("capture", DeviceState.Active);
        router.Removed(null);
        router.StateChanged("capture", DeviceState.Disabled);
        router.Removed("render");

        Assert.Equal([CaptureSourceKind.Microphone, CaptureSourceKind.RemotePlayback], changes);
    }

    [Fact]
    public void DefaultNotificationWinsOverOlderInitializationSnapshot()
    {
        var changes = new List<CaptureSourceKind>();
        var router = new EndpointChangeRouter(changes.Add);
        var prior = router.SnapshotToken(DataFlow.Capture);

        router.DefaultChanged(DataFlow.Capture, Role.Multimedia, "new-default");
        router.Initialize(DataFlow.Capture, prior, "stale-default");
        changes.Clear();
        router.Removed("stale-default");
        router.Removed("new-default");

        Assert.Equal([CaptureSourceKind.Microphone], changes);
    }

    [Fact]
    public void FaultySubscriberDoesNotPreventDeliveryToRemainingSubscribers()
    {
        var changes = new List<CaptureSourceKind>();
        Action<CaptureSourceKind> handlers = _ => throw new InvalidOperationException("test");
        handlers += changes.Add;

        EndpointChangeMonitor.DispatchHandlers(handlers, CaptureSourceKind.RemotePlayback);

        Assert.Equal([CaptureSourceKind.RemotePlayback], changes);
    }
}

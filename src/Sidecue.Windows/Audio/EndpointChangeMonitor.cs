using NAudio.CoreAudioApi;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Observes Multimedia default endpoint changes and loss of the selected defaults.</summary>
/// <remarks>
/// Create and dispose on a worker, never on a native audio notification callback. Notifications
/// are coalesced and dispatched on the thread pool so subscribers cannot block the Core Audio thread.
/// This monitor is caller-owned and does not contain or expose endpoint identifiers in its events.
/// Subscribers must handle their own failures; a throwing subscriber is isolated from other subscribers.
/// </remarks>
public sealed class EndpointChangeMonitor : IAudioEndpointChangeMonitor
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _client;
    private readonly EndpointChangeRouter _router;
    private int _pending;
    private int _scheduled;
    private int _disposed;

    public EndpointChangeMonitor()
    {
        if (SynchronizationContext.Current is not null)
        {
            throw new InvalidOperationException("Create the endpoint monitor on a worker without a synchronization context.");
        }
        _router = new EndpointChangeRouter(Signal);
        _enumerator = new MMDeviceEnumerator();
        try
        {
            _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            try
            {
                _client.DefaultDeviceChanged += DefaultChanged;
                _client.DeviceStateChanged += StateChanged;
                _client.DeviceRemoved += Removed;
                // Install callbacks first. A concurrent default change wins over a stale snapshot.
                InitializeDefault(DataFlow.Capture);
                InitializeDefault(DataFlow.Render);
            }
            catch
            {
                _client.Dispose();
                throw;
            }
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }
    }

    public event Action<CaptureSourceKind>? DefaultEndpointChanged;

    private void InitializeDefault(DataFlow flow)
    {
        var token = _router.SnapshotToken(flow);
        string? id = null;
        if (_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var device))
        {
            using (device) { id = device.ID; }
        }
        _router.Initialize(flow, token, id);
    }

    private void DefaultChanged(object? sender, DefaultDeviceChangedEventArgs e) =>
        _router.DefaultChanged(e.Flow, e.Role, e.DeviceId);

    private void StateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        _router.StateChanged(e.DeviceId, e.NewState);

    private void Removed(object? sender, DeviceNotificationEventArgs e) => _router.Removed(e.DeviceId);

    private void Signal(CaptureSourceKind kind)
    {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        Interlocked.Or(ref _pending, kind == CaptureSourceKind.Microphone ? 1 : 2);
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static monitor => monitor.Dispatch(), this, preferLocal: false);
        }
    }

    private void Dispatch()
    {
        try
        {
            var pending = Interlocked.Exchange(ref _pending, 0);
            if (Volatile.Read(ref _disposed) != 0) { return; }
            if ((pending & 1) != 0) { DispatchHandlers(DefaultEndpointChanged, CaptureSourceKind.Microphone); }
            if ((pending & 2) != 0) { DispatchHandlers(DefaultEndpointChanged, CaptureSourceKind.RemotePlayback); }
        }
        finally
        {
            Volatile.Write(ref _scheduled, 0);
            if (Volatile.Read(ref _pending) != 0 && Volatile.Read(ref _disposed) == 0) { Schedule(); }
        }
    }

    internal static void DispatchHandlers(Action<CaptureSourceKind>? handlers, CaptureSourceKind kind)
    {
        if (handlers is null) { return; }
        foreach (Action<CaptureSourceKind> handler in handlers.GetInvocationList())
        {
            try { handler(kind); }
            catch (Exception) { /* One consumer cannot terminate the process or block other consumers. */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        try
        {
            _client.DefaultDeviceChanged -= DefaultChanged;
            _client.DeviceStateChanged -= StateChanged;
            _client.DeviceRemoved -= Removed;
            _client.Dispose();
        }
        finally
        {
            _enumerator.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Routes native notification metadata without exposing opaque endpoint IDs.</summary>
internal sealed class EndpointChangeRouter(Action<CaptureSourceKind> signal)
{
    internal sealed record Snapshot(string? DeviceId);
    private Snapshot _microphone = new(null);
    private Snapshot _playback = new(null);

    internal Snapshot SnapshotToken(DataFlow flow) => flow switch
    {
        DataFlow.Capture => Volatile.Read(ref _microphone),
        DataFlow.Render => Volatile.Read(ref _playback),
        _ => throw new ArgumentOutOfRangeException(nameof(flow))
    };

    internal void Initialize(DataFlow flow, Snapshot token, string? deviceId)
    {
        var replacement = new Snapshot(deviceId);
        if (flow == DataFlow.Capture) { Interlocked.CompareExchange(ref _microphone, replacement, token); }
        else if (flow == DataFlow.Render) { Interlocked.CompareExchange(ref _playback, replacement, token); }
        else { throw new ArgumentOutOfRangeException(nameof(flow)); }
    }

    internal void DefaultChanged(DataFlow flow, Role role, string? deviceId)
    {
        if (role != Role.Multimedia) { return; }
        if (flow == DataFlow.Capture)
        {
            Interlocked.Exchange(ref _microphone, new Snapshot(deviceId));
            signal(CaptureSourceKind.Microphone);
        }
        else if (flow == DataFlow.Render)
        {
            Interlocked.Exchange(ref _playback, new Snapshot(deviceId));
            signal(CaptureSourceKind.RemotePlayback);
        }
    }

    internal void StateChanged(string? deviceId, DeviceState state)
    {
        if (state != DeviceState.Active) { Removed(deviceId); }
    }

    internal void Removed(string? deviceId)
    {
        if (deviceId is null) { return; }
        if (string.Equals(Volatile.Read(ref _microphone).DeviceId, deviceId, StringComparison.Ordinal))
        {
            signal(CaptureSourceKind.Microphone);
        }
        if (string.Equals(Volatile.Read(ref _playback).DeviceId, deviceId, StringComparison.Ordinal))
        {
            signal(CaptureSourceKind.RemotePlayback);
        }
    }
}

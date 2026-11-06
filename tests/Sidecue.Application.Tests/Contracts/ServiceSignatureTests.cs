using System.Runtime.CompilerServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Application.Tests.Contracts;

public sealed class ServiceSignatureTests
{
    [Fact]
    public async Task SmallSyntheticImplementationsUseThePublicInterfacesAndCancellationTokens()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var request = AssistanceContractTests.Request();
        var snapshot = new ContextSnapshot(request, AssistanceContractTests.Generation(), []);
        var write = new TranscriptWrite(ContractVersion.Current, ContractSamples.Final());
        var probe = new SignatureProbe(snapshot, write);
        ISessionClock clock = probe;
        IAudioCaptureSource source = probe;
        IStreamingTranscriber transcriber = probe;
        ISessionStore store = probe;
        IContextAssembler assembler = probe;
        IAssistanceProvider provider = probe;

        var capture = new CaptureRequest(ContractVersion.Current, ContractSamples.Identity(), clock.Anchor,
            CaptureAndStoreContractTests.Selection(), new AudioBufferPolicy(2_000_000, 64_000));
        var transcription = new TranscriptionRequest(ContractVersion.Current, ContractSamples.Identity(), "en",
            new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian),
            new ProviderConfigurationReference("synthetic-provider", "fixture-config-1"), clock.GetPosition());

        await using (var run = await source.OpenAsync(capture, token))
        {
            var frames = run.ReadFramesAsync(token);
            await foreach (var _ in run.ReadStatusAsync(token)) { }
            await foreach (var _ in transcriber.TranscribeAsync(transcription, frames, token)) { }
            await run.StopAsync(CaptureStopReason.End, token);
            Assert.Equal(CaptureState.Stopped, run.TerminalStatus?.State);
        }
        var receipt = await store.CommitTranscriptAsync(write, token);
        var stored = await store.ReadSessionAsync(ContractSamples.Session, token);
        var context = await assembler.BuildAsync(request, token);
        await foreach (var _ in provider.GenerateAsync(context, token)) { }

        Assert.True(probe.Disposed);
        Assert.Equal(clock.Anchor.UtcAtOrigin, clock.GetUtcNow());
        Assert.Equal(write.Event.Header.EventId, receipt.EventId);
        Assert.Equal(write, Assert.Single(stored.Entries).Write);
        Assert.Same(snapshot, context);
        Assert.Equal(9, probe.Tokens.Count);
        Assert.All(probe.Tokens, actual => Assert.Equal(token, actual));
    }

    // This stateless signature probe is intentionally not a replay/store model.
    // It exercises the boundaries without claiming provider or lifecycle behavior.
    private sealed class SignatureProbe(ContextSnapshot snapshot, TranscriptWrite write) :
        ISessionClock, IAudioCaptureSource, IAudioCaptureRun, IStreamingTranscriber,
        ISessionStore, IContextAssembler, IAssistanceProvider
    {
        public List<CancellationToken> Tokens { get; } = [];
        public bool Disposed { get; private set; }
        public SessionClockAnchor Anchor { get; } =
            new(ContractSamples.Session, ContractSamples.Position(), CaptureAndStoreContractTests.Utc());
        public CaptureStatusEvent? TerminalStatus { get; private set; }
        public TimelinePosition GetPosition() => Anchor.Origin;
        public UtcTimestamp GetUtcNow() => Anchor.UtcAtOrigin;

        public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Task.FromResult<IAudioCaptureRun>(this);
        }

        public IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync(CancellationToken ct)
        {
            Tokens.Add(ct);
            return Empty<OwnedAudioFrame>(ct);
        }

        public IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct)
        {
            Tokens.Add(ct);
            return Empty<CaptureStatusEvent>(ct);
        }

        public Task StopAsync(CaptureStopReason reason, CancellationToken ct)
        {
            Tokens.Add(ct);
            TerminalStatus = new CaptureStatusEvent(ContractVersion.Current, ContractSamples.Identity(),
                GetPosition(), CaptureState.Stopped);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TranscriptEvent> TranscribeAsync(TranscriptionRequest request,
            IAsyncEnumerable<OwnedAudioFrame> frames, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Empty<TranscriptEvent>(ct);
        }

        public Task<CommitReceipt> CommitTranscriptAsync(TranscriptWrite value, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Task.FromResult(new CommitReceipt(ContractVersion.Current, value.Event.Header.Source,
                value.Event.Header.EventId, new TranscriptCommitVersion(1), TranscriptCommitDisposition.Applied));
        }

        public Task<SessionSnapshot> ReadSessionAsync(SessionId id, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Task.FromResult(new SessionSnapshot(ContractVersion.Current, id, new TranscriptCommitVersion(1),
                [new CommittedTranscriptEntry(new TranscriptCommitVersion(1), write)]));
        }

        public Task<ContextSnapshot> BuildAsync(AssistRequest request, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Task.FromResult(snapshot);
        }

        public IAsyncEnumerable<AnswerEvent> GenerateAsync(ContextSnapshot value, CancellationToken ct)
        {
            Tokens.Add(ct);
            return Empty<AnswerEvent>(ct);
        }

        private static async IAsyncEnumerable<T> Empty<T>([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}

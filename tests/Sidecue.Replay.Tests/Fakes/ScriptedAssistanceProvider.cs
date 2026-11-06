using System.Runtime.CompilerServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Fakes;

/// <summary>Scripted callbacks, optionally ignoring cancellation to model late remote output.</summary>
internal sealed class ScriptedAssistanceProvider : IAssistanceProvider
{
    private readonly AnswerEvent[] _events;
    private readonly bool _ignoreCancellation;

    public ScriptedAssistanceProvider(IEnumerable<AnswerEvent> events, bool ignoreCancellation = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events.ToArray();
        foreach (var item in _events) { ArgumentNullException.ThrowIfNull(item); }
        _ignoreCancellation = ignoreCancellation;
    }

    public bool LastCancellationRequested { get; private set; }

    public async IAsyncEnumerable<AnswerEvent> GenerateAsync(ContextSnapshot snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LastCancellationRequested = cancellationToken.IsCancellationRequested;
        if (!_ignoreCancellation) { cancellationToken.ThrowIfCancellationRequested(); }
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var item in _events)
        {
            LastCancellationRequested = cancellationToken.IsCancellationRequested;
            if (!_ignoreCancellation) { cancellationToken.ThrowIfCancellationRequested(); }
            if (item.Header.Fence != snapshot.Fence)
            {
                throw new InvalidOperationException("Scripted output must retain its original request fence.");
            }
            yield return item;
        }
    }
}

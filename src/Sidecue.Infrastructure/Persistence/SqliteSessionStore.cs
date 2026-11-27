using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Persistence;

/// <summary>Serializes durable transcript operations on a bounded background worker.</summary>
public sealed class SqliteSessionStore : ISessionStore, IAsyncDisposable
{
    private const int PendingCapacity = 32;
    private readonly Channel<WorkItem> queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(PendingCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private int disposed;

    private SqliteSessionStore(SqliteConnection connection) =>
        worker = Task.Run(() => DrainAsync(connection));

    /// <summary>Opens the encrypted store and applies pending migrations on a background thread.</summary>
    /// <remarks>Session, timeline and stream identities must be registered before transcript writes.</remarks>
    public static Task<SqliteSessionStore> OpenAsync(
        EncryptedDatabaseStore database, string appVersion, bool createNew, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        return Task.Run(() =>
        {
            SqliteConnection connection = createNew
                ? database.CreateNewTranscriptStore(appVersion, cancellationToken)
                : database.OpenExistingTranscriptStore(appVersion, cancellationToken);
            connection.DefaultTimeout = 5;
            return new SqliteSessionStore(connection);
        }, cancellationToken);
    }

    public Task<CommitReceipt> CommitTranscriptAsync(TranscriptWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        return Enqueue(connection => TranscriptJournal.Commit(connection, write, ct), ct);
    }

    public Task<SessionSnapshot> ReadSessionAsync(SessionId id, CancellationToken ct)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A session identity is required.", nameof(id));
        }
        return Enqueue(connection => TranscriptJournal.Read(connection, id, ct), ct);
    }

    private Task<T> Enqueue<T>(Func<SqliteConnection, T> action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var item = new WorkItem<T>(action, ct);
        if (queue.Writer.TryWrite(item))
        {
            return item.Completion;
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return Task.FromException<T>(new EncryptedDatabaseException("The transcript store is busy and cannot accept another operation."));
    }

    private async Task DrainAsync(SqliteConnection connection)
    {
        using (connection)
        {
            await foreach (WorkItem item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                item.Execute(connection);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            queue.Writer.TryComplete();
        }
        return new ValueTask(worker);
    }

    private abstract class WorkItem
    {
        internal abstract void Execute(SqliteConnection connection);
    }

    private sealed class WorkItem<T>(Func<SqliteConnection, T> action, CancellationToken ct) : WorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<T> Completion => completion.Task;

        internal override void Execute(SqliteConnection connection)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                completion.SetResult(action(connection));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                completion.SetCanceled(ct);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }
    }
}

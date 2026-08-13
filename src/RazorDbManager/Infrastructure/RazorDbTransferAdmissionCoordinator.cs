using System.Collections.Concurrent;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class RazorDbTransferAdmissionCoordinator(IRazorDbJobStore jobs)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _databaseGates = new(StringComparer.Ordinal);

    public async ValueTask<IDisposable> EnterAsync(
        string databaseId,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        SemaphoreSlim gate = _databaseGates.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<RazorDbJobRecord> active = await jobs.ListAsync(
                new RazorDbJobQuery(databaseId, Limit: 100), cancellationToken).ConfigureAwait(false);
            if (active.Any(job => string.Equals(job.ActorId, actorId, StringComparison.Ordinal)
                    && job.Status is RazorDbJobStatus.Queued or RazorDbJobStatus.Running))
                throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "Only one active transfer job is allowed per user.");
            if (active.Count(job => job.Status is RazorDbJobStatus.Queued or RazorDbJobStatus.Running) >= 2)
                throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "Only two active transfer jobs are allowed for this database.");
            return new Lease(gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

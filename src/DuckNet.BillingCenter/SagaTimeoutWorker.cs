using DuckNet.EventBus;
using DuckNet.Kernel.Persistence;

namespace DuckNet.BillingCenter;

/// <summary>
/// Compensates Reserved sagas whose <c>expires_at</c> has passed. The UPDATE
/// WHERE state='Reserved' is the lock — AlarmResolved that already released
/// the row is a no-op here, and the reverse is also true.
/// </summary>
public sealed class SagaTimeoutWorker
{
    private readonly KernelDb _db;
    private readonly BillingStore _sagas;
    private readonly TimeProvider _time;
    private readonly PollingLoop _pollingLoop;
    private long _expiredCount;

    public SagaTimeoutWorker(
        KernelDb db,
        BillingStore sagas,
        TimeProvider? time = null,
        TimeSpan? pollInterval = null)
    {
        _db = db;
        _sagas = sagas;
        _time = time ?? TimeProvider.System;
        _pollingLoop = new PollingLoop(
            ExpireAvailableAsync,
            pollInterval ?? TimeSpan.FromMilliseconds(500));
    }

    public long ExpiredCount => Interlocked.Read(ref _expiredCount);

    public Task RunAsync(CancellationToken cancellationToken) => _pollingLoop.RunAsync(cancellationToken);

    public Task DrainAsync(CancellationToken cancellationToken = default) => _pollingLoop.DrainAsync(cancellationToken);

    private Task<int> ExpireAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = _db.Write((conn, tx) => _sagas.ExpireDue(conn, tx, _time.GetUtcNow()));
        if (n > 0)
        {
            Interlocked.Add(ref _expiredCount, n);
        }

        return Task.FromResult(n);
    }
}

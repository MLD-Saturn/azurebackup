using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Byte-level memory admission control for parallel chunk operations.
/// Each consumer acquires its actual byte cost before processing and releases when done.
/// This naturally allows more small chunks and fewer large chunks concurrently,
/// using the budget precisely rather than assuming worst-case chunk sizes for all slots.
/// <para>
/// The "at-least-one" guarantee prevents deadlock when a single chunk's cost exceeds
/// the remaining budget (e.g., a 128 MB chunk when only 90 MB remains free).
/// When nothing is in-flight, one operation is always allowed regardless of its cost.
/// </para>
/// <para>
/// Pass <see cref="long.MaxValue"/> for an unlimited budget — acquire/release become
/// no-ops, preserving the current uncapped behavior with zero overhead.
/// </para>
/// </summary>
public sealed class MemoryBudget : IDisposable
{
    private readonly long _totalBytes;
    private readonly bool _isUnlimited;
    private long _usedBytes;
    private long _peakUsedBytes;
    private int _waitersCount;
    private long _stallCount;
    private long _oversizedAdmissions;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _budgetReleased = new(0, int.MaxValue);

    /// <summary>Bytes currently acquired by active operations.</summary>
    public long UsedBytes
    {
        get { lock (_lock) { return _usedBytes; } }
    }

    /// <summary>
    /// B56 (W3 Phase F): high-water mark of <see cref="UsedBytes"/> over
    /// the lifetime of this budget. Updated inside the same critical
    /// section that mutates <c>_usedBytes</c> so the value is always
    /// internally consistent. Surfaced through
    /// <see cref="BackupMemoryReporter"/> so a post-hoc reading of the
    /// log line shows whether the operation actually saturated the
    /// configured ceiling, distinct from the instantaneous current usage.
    /// </summary>
    public long PeakUsedBytes => Volatile.Read(ref _peakUsedBytes);

    /// <summary>Bytes remaining in the budget.</summary>
    public long RemainingBytes => _totalBytes - UsedBytes;

    /// <summary>Total budget capacity in bytes.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>True when the budget is unlimited (no throttling).</summary>
    public bool IsUnlimited => _isUnlimited;

    /// <summary>
    /// Number of times <see cref="AcquireAsync"/> had to wait because the budget was full.
    /// Reset when <see cref="ResetStallCount"/> is called. Thread-safe.
    /// </summary>
    public long StallCount => Volatile.Read(ref _stallCount);

    /// <summary>
    /// B62: live count of acquirers currently parked in <see cref="AcquireAsync"/>'s slow
    /// path waiting for budget to free up. Surfaced so a stall watchdog can distinguish a
    /// "budget saturated, waiters parked" deadlock from a "no one is asking" idle pipeline.
    /// A non-zero value while no chunks are being written is a structural deadlock signal.
    /// Thread-safe.
    /// </summary>
    public int WaitersCount => Volatile.Read(ref _waitersCount);

    /// <summary>
    /// B34: number of acquisitions that bypassed the cap because a single
    /// request exceeded the entire budget (and would otherwise deadlock).
    /// A non-zero value means the user's configured ceiling was breached
    /// at least once during the operation; <see cref="BackupMemoryReporter"/>
    /// surfaces this in periodic samples so the breach is visible without
    /// having to wait for the operation summary. Thread-safe.
    /// </summary>
    public long OversizedAdmissions => Volatile.Read(ref _oversizedAdmissions);

    /// <summary>
    /// Resets the stall counter to zero. Call at the start of each operation
    /// to get per-operation stall counts.
    /// </summary>
    public void ResetStallCount() => Interlocked.Exchange(ref _stallCount, 0);

    /// <summary>
    /// Creates a new memory budget with the specified capacity.
    /// </summary>
    /// <param name="totalBytes">
    /// Total budget in bytes. Pass <see cref="long.MaxValue"/> for unlimited (no throttling).
    /// </param>
    public MemoryBudget(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        _totalBytes = totalBytes;
        _isUnlimited = totalBytes == long.MaxValue;
    }

    /// <summary>
    /// Acquires <paramref name="bytes"/> from the budget, waiting if necessary.
    /// <para>
    /// B34: the deadlock-avoidance branch admits a request that exceeds
    /// the remaining budget ONLY when the request itself is larger than
    /// the entire budget AND no other operation is currently in flight.
    /// Pre-B34 the branch fired whenever <c>_usedBytes == 0</c>, which on a
    /// long-running parallel backup could happen repeatedly between
    /// drain cycles -- each oversized admission then bypassed the cap and
    /// the actual residency drifted past the configured ceiling. The
    /// new check guarantees a single chunk that legitimately cannot fit
    /// (e.g. a 128 MB CDC payload on a 64 MB budget) is still admitted,
    /// while a chunk that COULD fit if it waited will wait, even when
    /// the budget happens to read empty at this instant. Each oversized
    /// admission increments <see cref="OversizedAdmissions"/> so the
    /// breach is observable rather than silent.
    /// </para>
    /// </summary>
    /// <param name="bytes">Cost in bytes (typically chunkSize × 2 for encrypt, × 3 for download).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AcquireAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_isUnlimited)
            return;

        // Fast path: try to acquire without waiting
        lock (_lock)
        {
            if (_usedBytes + bytes <= _totalBytes)
            {
                _usedBytes += bytes;
                if (_usedBytes > _peakUsedBytes) _peakUsedBytes = _usedBytes;
                return;
            }

            // B34: deadlock-avoidance only fires for genuinely oversized
            // requests when nothing else is in flight. A request that
            // could fit by waiting must wait, even when the budget is
            // momentarily empty.
            if (bytes > _totalBytes && _usedBytes == 0)
            {
                _usedBytes += bytes;
                if (_usedBytes > _peakUsedBytes) _peakUsedBytes = _usedBytes;
                Interlocked.Increment(ref _oversizedAdmissions);
                return;
            }
        }

        // Slow path: wait for budget to free up
        Interlocked.Increment(ref _stallCount);
        Interlocked.Increment(ref _waitersCount);
        try
        {
            while (true)
            {
                await _budgetReleased.WaitAsync(cancellationToken);

                lock (_lock)
                {
                    if (_usedBytes + bytes <= _totalBytes)
                    {
                        _usedBytes += bytes;
                        if (_usedBytes > _peakUsedBytes) _peakUsedBytes = _usedBytes;
                        return;
                    }

                    if (bytes > _totalBytes && _usedBytes == 0)
                    {
                        _usedBytes += bytes;
                        if (_usedBytes > _peakUsedBytes) _peakUsedBytes = _usedBytes;
                        Interlocked.Increment(ref _oversizedAdmissions);
                        return;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waitersCount);
        }
    }

    /// <summary>
    /// Returns <paramref name="bytes"/> to the budget, waking any waiting acquirers.
    /// </summary>
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_isUnlimited)
            return;

        lock (_lock)
        {
            _usedBytes = Math.Max(0, _usedBytes - bytes);
        }

        // Wake all waiting acquirers so they can re-check whether their request fits.
        var waiters = Volatile.Read(ref _waitersCount);
        if (waiters > 0)
        {
            try { _budgetReleased.Release(waiters); }
            catch (SemaphoreFullException) { /* already signaled */ }
        }
    }

    /// <summary>
    /// B72 (W5 Phase 4): synchronous, non-blocking attribution of pool-cache
    /// retention to the budget. Unlike <see cref="AcquireAsync"/>, this never
    /// waits and never refuses -- the caller (currently
    /// <see cref="ChunkBufferPool.Return"/>) is on a hot path that cannot
    /// block the consumer. The budget total may temporarily exceed
    /// <see cref="TotalBytes"/> while the pool sits at its configured cap;
    /// the cap on <see cref="ChunkBufferPool.MaxCachedBytes"/> bounds the
    /// overshoot, and the producer's next <see cref="AcquireAsync"/> call
    /// will simply wait until enough cached buffers have been rented out
    /// (each rent invoking <see cref="ReleaseRetention"/>) to free
    /// headroom. <see cref="PeakUsedBytes"/> tracks the elevated total so
    /// the operator sees the pool's residency reflected in the same
    /// metric that already tracked in-flight chunks.
    /// </summary>
    /// <remarks>
    /// Does NOT increment <see cref="StallCount"/> or
    /// <see cref="OversizedAdmissions"/> -- both metrics are reserved for
    /// chunk-admission events on the <see cref="AcquireAsync"/> path.
    /// Does NOT wake waiters -- a charge can only reduce headroom, so
    /// there is never a waiter that could newly succeed.
    /// </remarks>
    /// <param name="bytes">Bytes to attribute. Must be positive.</param>
    public void ChargeRetention(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_isUnlimited)
            return;

        lock (_lock)
        {
            _usedBytes += bytes;
            if (_usedBytes > _peakUsedBytes) _peakUsedBytes = _usedBytes;
        }
    }

    /// <summary>
    /// B72 (W5 Phase 4): release pool-cache retention attributed by an
    /// earlier <see cref="ChargeRetention"/>. Symmetrical with
    /// <see cref="Release"/> but kept as a distinct entry point so the
    /// pool's call sites read clearly and a future debugger can tell a
    /// rent-from-cache release apart from a chunk-completion release.
    /// Wakes any waiting acquirers so they can re-check; releasing
    /// retention is the exact event that frees the headroom a stalled
    /// producer was waiting on.
    /// </summary>
    /// <param name="bytes">Bytes to release. Must be positive.</param>
    public void ReleaseRetention(long bytes) => Release(bytes);

    /// <summary>
    /// Creates a budget from the user's configuration.
    /// Returns an unlimited budget when memory limiting is disabled.
    /// Reserves <paramref name="fixedOverheadBytes"/> for non-chunk allocations
    /// (CDC buffer, file streams, etc.) so the budget reflects only the
    /// memory available for in-flight chunks.
    /// </summary>
    /// <param name="config">Backup configuration with memory limit settings.</param>
    /// <param name="fixedOverheadBytes">
    /// Bytes reserved for fixed allocations that are always present during the operation.
    /// </param>
    public static MemoryBudget FromConfig(BackupConfiguration config, long fixedOverheadBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedOverheadBytes);

        if (!config.MemoryLimitEnabled)
            return new MemoryBudget(long.MaxValue);

        var totalBytes = (long)config.MemoryLimitMB * 1024 * 1024;
        var available = Math.Max(totalBytes - fixedOverheadBytes, 1);
        return new MemoryBudget(available);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _budgetReleased.Dispose();
    }
}

using System.Threading.Channels;

namespace AzureBackup.Core.Services;

/// <summary>
/// I6 (post-D7 audit): drains
/// <see cref="IBlobStorageService.OnChunkUploaded"/> callbacks off the
/// upload thread so a slow <see cref="LocalDatabaseService.SetChunkExpectedMd5"/>
/// (large WAL flush, contended write lock) cannot back-pressure the
/// upload pipeline. The callback enqueues to an unbounded
/// <see cref="Channel{T}"/> in O(1) and returns immediately; a single
/// background task drains the queue and persists each entry.
/// </summary>
/// <remarks>
/// Single-reader is intentional: the SqliteBackend's write lock would
/// serialize concurrent SetChunkExpectedMd5 calls anyway, so multi-
/// reader buys us nothing but extra context switches. <see cref="DisposeAsync"/>
/// completes the channel writer and awaits the reader so any buffered
/// MD5s get flushed during app shutdown.
/// </remarks>
public sealed class ExpectedMd5Drain : IAsyncDisposable
{
    private readonly LocalDatabaseService _databaseService;
    private readonly Channel<(string chunkHash, byte[] md5)> _queue;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _shutdownCts = new();

    public ExpectedMd5Drain(LocalDatabaseService databaseService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        _databaseService = databaseService;
        _queue = Channel.CreateUnbounded<(string, byte[])>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // Multi-writer because the upload pool is N-way parallel.
            SingleWriter = false
        });
        _drainTask = Task.Run(DrainLoopAsync);
    }

    /// <summary>
    /// Enqueue an upload-time MD5 for background persistence. Safe to
    /// call from any thread; returns in nanoseconds even when the
    /// reader is slow.
    /// </summary>
    public void Enqueue(string chunkHash, byte[] md5)
    {
        if (string.IsNullOrWhiteSpace(chunkHash) || md5 == null || md5.Length != 16) return;
        // TryWrite is the synchronous path on an unbounded channel; only
        // returns false if the writer has already been completed (e.g.,
        // we're past DisposeAsync). Drop silently in that case -- the
        // upload already succeeded and the app is shutting down.
        _queue.Writer.TryWrite((chunkHash, md5));
    }

    private async Task DrainLoopAsync()
    {
        try
        {
            await foreach (var (hash, md5) in _queue.Reader.ReadAllAsync(_shutdownCts.Token))
            {
                try
                {
                    _databaseService.SetChunkExpectedMd5(hash, md5);
                }
                catch (Exception)
                {
                    // Best-effort: a single failed MD5 stamp does not
                    // justify killing the drain or rolling back uploads.
                    // The chunk's expected_encrypted_md5 stays null and
                    // the legacy backfill scan (D10) can promote it later.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path; no recovery needed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop accepting new entries, then await the reader so any
        // already-queued items get persisted before app exit.
        _queue.Writer.TryComplete();
        try { await _drainTask.ConfigureAwait(false); } catch { /* shutdown best effort */ }
        _shutdownCts.Dispose();
    }
}

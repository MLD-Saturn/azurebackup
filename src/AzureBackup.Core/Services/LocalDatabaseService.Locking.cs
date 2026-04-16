namespace AzureBackup.Core.Services;

/// <summary>
/// Reader / writer lock helpers for <see cref="LocalDatabaseService"/>.
/// Kept in a separate partial so the primary file stays focused on public API.
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Runs <paramref name="body"/> inside a read lock. Readers run concurrently
    /// with other readers; they block only if a writer is active or pending.
    /// </summary>
    private T InReadLock<T>(Func<T> body)
    {
        _dbLock.EnterReadLock();
        try { return body(); }
        finally { _dbLock.ExitReadLock(); }
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a write lock. Writers are exclusive:
    /// they block until no readers or other writers are active.
    /// </summary>
    private void InWriteLock(Action body)
    {
        _dbLock.EnterWriteLock();
        try { body(); }
        finally { _dbLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a write lock and returns its result.
    /// </summary>
    private T InWriteLock<T>(Func<T> body)
    {
        _dbLock.EnterWriteLock();
        try { return body(); }
        finally { _dbLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside an upgradeable read lock. Use when the
    /// caller first reads to decide whether to write, then may need to upgrade.
    /// Only one upgradeable reader may hold the lock at a time, but normal readers
    /// may proceed concurrently. Call <see cref="ReaderWriterLockSlim.EnterWriteLock"/>
    /// inside <paramref name="body"/> when the write is actually needed.
    /// </summary>
    private void InUpgradeableReadLock(Action body)
    {
        _dbLock.EnterUpgradeableReadLock();
        try { body(); }
        finally { _dbLock.ExitUpgradeableReadLock(); }
    }
}

using static AzureBackup.Core.ByteSizes;

namespace AzureBackup.Core.Services;

/// <summary>
/// Small shared helpers for the Argon2id key-derivation paths in
/// <see cref="EncryptionService"/> and <see cref="Backends.SqliteBackend"/>.
/// Both paths make the same 8 MB-per-lane Large Object Heap allocations, log the
/// same process-memory snapshots, and run the same LOH-compaction recovery on
/// <see cref="OutOfMemoryException"/>; this type removes the duplicated GC dance
/// and the repeated bytes-to-megabytes conversion so the two call sites cannot
/// drift apart.
/// </summary>
internal static class KdfMemoryDiagnostics
{
    /// <summary>
    /// Converts a byte count to whole megabytes for diagnostic log lines.
    /// Replaces the <c>x / (1024 * 1024)</c> literal that was repeated across
    /// every KDF memory-state message.
    /// </summary>
    public static long ToMegabytes(long bytes) => bytes / MB;

    /// <summary>
    /// Forces a blocking, compacting Large Object Heap collection. Called once
    /// after an Argon2id <see cref="OutOfMemoryException"/> to defragment the LOH
    /// before the single retry. This never changes the derived key (the Argon2id
    /// parameters are untouched); it only tries to make room for the same
    /// allocation that just failed. The exact sequence -- set
    /// <see cref="System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce"/>,
    /// force a blocking compacting gen-2 collect, drain finalizers, then collect
    /// again to reclaim anything the finalizers freed -- was previously inlined
    /// identically at three call sites.
    /// </summary>
    public static void ForceLargeObjectHeapCompaction()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

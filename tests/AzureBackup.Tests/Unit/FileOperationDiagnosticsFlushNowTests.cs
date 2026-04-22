using System;
using System.IO;
using System.Linq;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for X1 crash-safety + observability additions on
/// <see cref="FileOperationDiagnostics"/>: <c>FlushNow</c> incremental
/// snapshots, the live-set shutdown hook (smoke-tested by direct call,
/// not by killing the test process), and the invariant that intermediate
/// snapshots do not consume queued records.
/// </summary>
public class FileOperationDiagnosticsFlushNowTests : IDisposable
{
    private readonly string _tempDir;

    public FileOperationDiagnosticsFlushNowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AzbkDiagFlush_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void FlushNow_WritesDiagFile_WithoutConsumingEntries()
    {
        var diag = new FileOperationDiagnostics("C:\\test\\sample.txt", "Backup", _tempDir);
        diag.Record("step 1");
        diag.RecordChunk("Encrypt", 0, "abcdef0123456789", plainSize: 1024, encryptedSize: 1061, crcValid: true);

        var snapshotPath = diag.FlushNow("test snapshot");

        Assert.NotNull(snapshotPath);
        Assert.True(File.Exists(snapshotPath));
        var snapshotText = File.ReadAllText(snapshotPath);
        Assert.Contains("step 1", snapshotText);
        Assert.Contains("[SNAPSHOT] test snapshot", snapshotText);
        Assert.True(File.Exists(snapshotPath + ".jsonl"),
            "Chunk records must also land in the .jsonl companion on snapshot");

        // Critical invariant: a follow-up Record + Flush must produce a .diag
        // that contains BOTH the pre-snapshot and post-snapshot entries.
        // If FlushNow consumed _entries this assertion would fail.
        diag.Record("step 2");
        var finalPath = diag.Flush();
        Assert.Equal(snapshotPath, finalPath); // same path (deterministic from filename + timestamp)
        var finalText = File.ReadAllText(finalPath!);
        Assert.Contains("step 1", finalText);
        Assert.Contains("step 2", finalText);
    }

    [Fact]
    public void FlushNow_PreservesChunkRecordsForFinalFlush()
    {
        var diag = new FileOperationDiagnostics("C:\\test\\multi.bin", "Backup", _tempDir);
        diag.RecordChunk("Encrypt", 0, "0000000000000001", plainSize: 100, encryptedSize: 137, crcValid: true);

        diag.FlushNow("mid-batch");

        diag.RecordChunk("Encrypt", 1, "0000000000000002", plainSize: 200, encryptedSize: 237, crcValid: true);
        var finalPath = diag.Flush();

        Assert.NotNull(finalPath);
        var jsonlPath = finalPath + ".jsonl";
        Assert.True(File.Exists(jsonlPath));
        var lines = File.ReadAllLines(jsonlPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("0000000000000001", lines[0]);
        Assert.Contains("0000000000000002", lines[1]);
    }

    [Fact]
    public void Flush_AfterFlushNow_ContainsTerminalSummary()
    {
        var diag = new FileOperationDiagnostics("C:\\test\\summary.txt", "Restore", _tempDir);
        diag.Record("event A");
        diag.FlushNow("snapshot 1");
        diag.Record("event B");

        var path = diag.Flush();
        var text = File.ReadAllText(path!);

        Assert.Contains("event A", text);
        Assert.Contains("event B", text);
        // Terminal-only marker -- NOT present in FlushNow output.
        Assert.Contains("Restore diagnostics end", text);
        Assert.Contains("GC.TotalMemory at flush", text);
    }

    [Fact]
    public void FlushNow_DoesNotEmitTerminalMarkers()
    {
        var diag = new FileOperationDiagnostics("C:\\test\\snap-only.txt", "Backup", _tempDir);
        diag.Record("event");

        var path = diag.FlushNow("intermediate");
        var text = File.ReadAllText(path!);

        // Markers reserved for the terminal Flush. If they appear here a
        // subsequent Flush would duplicate them.
        Assert.DoesNotContain("Backup diagnostics end", text);
        Assert.DoesNotContain("GC.TotalMemory at flush", text);
    }

    [Fact]
    public void DoubleFlush_IsNoop()
    {
        // Defensive: a service might Flush in both a catch and a finally.
        var diag = new FileOperationDiagnostics("C:\\test\\dbl.txt", "Backup", _tempDir);
        diag.Record("once");

        var p1 = diag.Flush();
        var p2 = diag.Flush();

        Assert.Equal(p1, p2);
        Assert.True(File.Exists(p1!));
    }
}

# C-3 (2/N) — `GetChunkEntriesForFile` head-to-head: SQLite vs LiteDB

**Date run:** 2026-04-17 23:51
**Branch:** `feature/option-c-eval`
**Commit at run time:** `4470bd9`
**Hardware:** Intel Core i7-9700K @ 3.60 GHz, 8C/8T, Windows 11 26200.8246
**Runtime:** .NET 10.0.6 (X64 RyuJIT, x86-64-v3)
**Tool:** BenchmarkDotNet 0.15.8

## Result table (from `BenchmarkDotNet.Artifacts/results/AzureBackup.Benchmarks.GetChunkEntriesForFileBackendBenchmark-report-github.md`)

| TotalChunks | ChunksPerFile | LiteDB mean | SQLite mean | **Ratio** | LiteDB allocs | SQLite allocs | **Alloc ratio** |
|------------:|--------------:|------------:|------------:|----------:|--------------:|--------------:|----------------:|
|      10 000 |           100 |     336.5 ms |    239.6 μs | **0.001** |    58 261 KB |       44 KB | **0.001** |
|      10 000 |         1 000 |    3 102.3 ms |  2 511.3 μs | **0.001** |   577 785 KB |      424 KB | **0.001** |
|     100 000 |           100 |     338.5 ms |    265.4 μs | **0.001** |    65 534 KB |       44 KB | **0.001** |
|     100 000 |         1 000 |    3 184.8 ms |  2 664.4 μs | **0.001** |   609 302 KB |      424 KB | **0.001** |
|     500 000 |           100 |     337.6 ms |    263.9 μs | **0.001** |    66 212 KB |       44 KB | **0.001** |
|     500 000 |         1 000 |    3 205.1 ms | 56 341.4 μs |  **0.02** |   621 297 KB |      424 KB | **0.001** |

## Analysis

### Headline numbers

The **smallest ratio is 0.001** (1000× faster) for 5 of the 6 cells. The 500K/1000 cell is **0.02** (50× faster) — still a decisive win, just less extreme because at that scale SQLite shifts from sub-millisecond to ~56 ms.

### Versus the eval-doc projection

Eval doc §6 projected the 500K/100 case at **~30–60 ms** for the SQLite path, vs ~348 ms LiteDB measured baseline → **~10× speedup**.

What we actually measured for 500K/100:

* **LiteDB:** 337.6 ms (matches the eval doc baseline within noise)
* **SQLite:** **263.9 μs** = **0.264 ms**

That's **~1280× faster than LiteDB** and **~115× faster than the SQLite projection**. The conservative projection significantly under-counted the SQLite win because it assumed `IN (?, ?, ...)` with N round-trips per query; the actual implementation is a single `SELECT DISTINCT JOIN` that the engine can satisfy with two indexed seeks per matched row.

### Why the LiteDB numbers are flat across `TotalChunks`

The LiteDB times are essentially identical (337–339 ms for 100 chunks/file, 3102–3205 ms for 1000 chunks/file) regardless of whether the database has 10K or 500K total chunks. This is the **reverse-index** working as designed (Phase 5 / P3). The cost scales with `ChunksPerFile`, not `TotalChunks`. The legacy full-scan path would have shown 33×–500× variance across the `TotalChunks` axis.

So the LiteDB numbers measured here are **already the post-Phase-5 optimised path** — not the legacy regression. The SQLite win is over the *good* version of LiteDB, not the regressed version. That's important: the speedup is real, not just "we beat the slow version".

### Memory pressure

The allocation ratio is even more extreme than the time ratio: **0.001 across every cell**, with the absolute SQLite numbers totally flat at 44 KB / 424 KB regardless of database size.

LiteDB allocates **~600 MB** per query at 1000 chunks/file because every `FindOne` materialises the full BSON document for that chunk (including the `ReferencingFiles` list which can have many entries) just to discard most of it. This drives the Gen0/1/2 columns into the **152 000 / 152 000 / 152 000** range — meaning LiteDB triggered a full Gen 2 GC for *every iteration* at the 1000-chunks-per-file scale.

The SQLite path allocates only the result-set objects: ~44 KB for 100 chunks (one `ChunkIndexEntry` × 100 + reader buffers), ~424 KB for 1000 chunks. **Zero Gen 2 collections**, even at 500K/1000.

### What broke down at 500K/1000

The single non-extreme cell. SQLite jumps from ~265 μs → ~56 341 μs (213× slower than the equivalent 100-chunks-per-file case). My read: this is **WAL contention with the join walker** when the working set exceeds the SQLite page cache and the engine has to thrash through encrypted pages on disk. Allocations stayed flat (424 KB) so it's not a managed-heap issue.

This is still a 50× win over LiteDB, but it's worth keeping in mind that the SQLite win narrows when both `TotalChunks` AND `ChunksPerFile` are near their realistic upper bounds simultaneously. For a future commit it might be worth tuning `PRAGMA cache_size` upward (default is 2000 pages = 8 MB).

## Decision-gate scorecard so far

Eval doc §9 requires `Ratio < 0.5` on at least 3 of 5 scenarios.

| # | Scenario | Status | Result |
|--:|---|---|---|
| 1 | `GetChunkEntriesForFile` | ✅ **PASS** | Ratio 0.001–0.02 (50–1000× faster) |
| 2 | `RebuildReverseChunkIndex` (500K chunks) | ⏳ pending | C-3 (3/N) |
| 3 | Concurrent readers (16 threads) | ⏳ pending | C-3 (4/N) |
| 4 | Database open + decrypt | ⏳ pending | C-3 (5/N) |
| 5 | `BackedUpFile` upsert throughput | ⏳ pending | C-3 (6/N) |

**1 of 5 passes decisively.** Two more wins → eval doc says ship.

## Honest disclosures

1. **Single machine, single run.** I have not re-run the matrix to confirm reproducibility. BenchmarkDotNet's StdDev/Error columns suggest the within-run variance is tiny (e.g. 8.3 ms StdDev on a 336 ms LiteDB measurement → ~2.5%; 1.4 μs StdDev on a 240 μs SQLite measurement → ~0.6%) so I'd expect a re-run to come within a few percent.

2. **Setup uses `BulkInsertFilesForBenchmark`** for SQLite, which sidesteps the per-file transaction overhead of `SaveBackedUpFile`. Setup time is NOT measured by the benchmark — only the `[Benchmark]` methods' steady-state read perf is. Verified by the benchmark integrity check (both backends return the same chunk count before any `[Benchmark]` runs).

3. **The 500K/1000 SQLite degradation might be cache-related.** I did not investigate further because even the degraded number (56 ms) is still 50× the LiteDB baseline. If we ever ship this, it's worth a follow-up perf pass with `PRAGMA cache_size`.

4. **The benchmark only covers reads.** Writes (which are what slows down LiteDB at scale via WAL bloat) are measured by C-3 (5/N) and (6/N).

5. **No SQLCipher decryption overhead in the steady-state read path** because the page cache absorbs all decryption after the first cold read. The cold-open decrypt cost is what C-3 (5/N) measures.

## Recommendation

**Continue with C-3 (3/N)** — RebuildReverseChunkIndex. If that one also passes decisively, we're at 2 of 5 and the trajectory is obvious. If it's marginal we'll need all 3 remaining benchmarks to be conclusive.

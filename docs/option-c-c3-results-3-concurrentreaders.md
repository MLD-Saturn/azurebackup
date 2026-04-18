# C-3 (4b) — Concurrent readers head-to-head: decisive SQLite win

**Date run:** 2026-04-18 01:31
**Branch:** `feature/option-c-eval`
**Commit at run time:** `163a0f4` (the C-3 (4a) Argon2id-via-factory + lock fix)
**Hardware:** Intel Core i7-9700K @ 3.60 GHz, 8C/8T, Windows 11 26200.8246
**Runtime:** .NET 10.0.6 (X64 RyuJIT, x86-64-v3)
**Tool:** BenchmarkDotNet 0.15.8

## Result table (from `BenchmarkDotNet.Artifacts/results/AzureBackup.Benchmarks.ConcurrentReadersBackendBenchmark-report-github.md`)

| Readers | LiteDB | SQLiteSingleConn | SQLitePooled | **Pooled / LiteDB ratio** | **SingleConn / LiteDB ratio** |
|--------:|-------:|-----------------:|-------------:|--------------------------:|------------------------------:|
|       1 |   330.75 ms |    0.313 ms |    0.308 ms |  **0.0009** (1 074× faster) |  **0.0009** (1 057× faster) |
|       4 |  1 428.5 ms |    1.373 ms |    0.388 ms |  **0.0003** (3 682× faster) |  **0.0010** (1 040× faster) |
|      16 |  5 820.6 ms |   52.461 ms |    0.800 ms |  **0.0001** (7 275× faster) |  **0.0090** (    111× faster) |

| Readers | LiteDB allocs | SQLitePooled allocs | **Alloc ratio** | LiteDB Gen 2 | SQLite Gen 2 |
|--------:|--------------:|--------------------:|----------------:|-------------:|-------------:|
|       1 |     65 542 KB |              44 KB | **0.0007** |    16 000 |    **0** |
|       4 |    263 681 KB |             177 KB | **0.0007** |    65 000 |    **0** |
|      16 |  1 054 033 KB |             707 KB | **0.0007** |   261 000 |    **0** |

## Analysis

### This is a decisive pass

Eval doc §9 threshold is **`Ratio < 0.5`**. SQLitePooled clears it by **3–4 orders of magnitude** at every cell. Even the "honest" SQLiteSingleConn lock-serialized leg passes the gate decisively at every cell — 1057×, 1040×, 111× faster than LiteDB.

### Three findings worth pulling out

**1. The pool scales near-linearly with reader count.**

```
Pool @  1 reader :  308 μs   (baseline)
Pool @  4 readers:  388 μs   (1.26× of baseline for 4× the work)
Pool @ 16 readers:  800 μs   (2.60× of baseline for 16× the work)
```

For 16× the work the wall-clock grows only 2.6×. WAL mode is doing exactly what the docs say it does: multiple readers don't block each other. The remaining slowdown is hardware (we have 8 physical cores; the benchmark fans out 16 threads, so the second 8 wait for cores).

**2. LiteDB scales linearly with reader count and degrades catastrophically.**

```
LiteDB @  1 reader :  330 ms
LiteDB @  4 readers: 1428 ms  (4.32× of baseline for 4× the work)
LiteDB @ 16 readers: 5820 ms  (17.6× of baseline for 16× the work)
```

The LiteDB numbers grow strictly with the number of readers because every reader serializes through the `LocalDatabaseService`'s `ReaderWriterLockSlim` (multiple readers can hold the lock concurrently, but every reader still hits the same single LiteDB connection underneath, and LiteDB serializes inside that connection). At 16 readers the wall-clock is **5.8 seconds** — a UI hang the user will absolutely notice if anything queries chunk metadata.

**3. Even SQLiteSingleConn (lock-serialized, no pool) crushes LiteDB.**

The honest as-shipped story is the SingleConn row. At 16 readers it's 52 ms vs LiteDB's 5820 ms — **111× faster** with a single connection and a `lock` around it. So:

* If we ship SQLite **without** the pool we still win by 100×+ on this scenario.
* If we ship SQLite **with** the pool we win by 7000×+.

The pool is a future optimisation; the gate passes either way.

### Memory pressure

Same overwhelming story as the other scenarios: at 16 readers LiteDB allocates **1 GB per single iteration** (yes, per iteration, not per second). 261 000 Gen 2 GCs per iteration. SQLite holds steady at 707 KB and **zero** Gen 2 collections.

The real-world implication: if a user has the file-list pane open and the storage-health pane refreshing in the background while a backup runs, today's LiteDB build is constantly thrashing the GC. A pure SQLite build wouldn't.

### Why SingleConn @ 4 readers (1.37 ms) is 4× SingleConn @ 1 reader (0.31 ms)

Confirms the lock works. Four threads × 0.3 ms each ≈ 1.2 ms serialized, plus a bit of lock-acquisition overhead. Exactly what we'd expect.

### Why SingleConn @ 16 readers (52 ms) is much worse than 16× the single-reader case

16 readers × 0.31 ms each ≈ 5 ms if serialized cleanly. Actual: 52 ms — **10× the lock-serialized expected cost**. Likely explanations:

* Lock contention overhead at 16 contending threads
* SQLite internal mutex on the single connection adds further contention
* Thread context-switch cost dominates at this fan-out

This is the cost of **not** building the pool. The 52 ms still passes the gate (vs LiteDB's 5820 ms) but it's a clear signal that **if Option C ships, building the pool is worth it** — the difference between SingleConn and Pooled at 16 readers is 65× in SQLite's favour.

## Decision-gate scorecard so far

Eval doc §9 requires `Ratio < 0.5` on at least 3 of 5 scenarios.

| # | Scenario | Status | Result |
|--:|---|---|---|
| 1 | `GetChunkEntriesForFile` | ✅ **PASS** | 0.001–0.02 (50–1000× faster) |
| 2 | `RebuildReverseChunkIndex` | ⚠ MARGINAL | 0.224 / 0.575 / 0.572 (1 of 3 cells passes) |
| 3 | Concurrent readers | ✅ **PASS** | 0.0001–0.009 (111–7275× faster) |
| 4 | Database open + decrypt | ⏳ pending | C-3 (5/N) |
| 5 | `BackedUpFile` upsert throughput | ⏳ pending | C-3 (6/N) |

**2 of 5 decisive passes. We need 1 more from the remaining 2 to hit the 3-of-5 ship threshold.**

## Honest disclosures

1. **The pool benchmark uses pre-derived Argon2id keys**. Each pool connection re-runs Argon2id at construction, which takes ~50–100 ms per connection. That cost is in `GlobalSetup`, not in the `[Benchmark]` measurement, so it does not bias the timing numbers. But a production pool would need a key cache to avoid paying that cost on every pool refill. Out of scope here; documented for future C-1 final step b.

2. **The previous run's `SQLiteSingleConn @ 16 = 69 ms` number from C-3 (4/N)** was measured under the racy code path before the lock was added in C-3 (4a). The current `52 ms` number is the lock-serialized correct measurement. Discard the 69 ms; trust the 52 ms.

3. **One run, no reproducibility re-test.** SQLite's StdDev is 1–6% on every cell; the qualitative picture (3-4 orders of magnitude faster) is rock-solid. LiteDB's StdDev is < 1.2% — also tight. Numbers are precise.

4. **Reader/writer contention is NOT measured.** This is pure-read concurrency. The actual production hotspot Phase 5 / P2 was motivated by — readers waiting on a writer — could behave differently. WAL mode should still win there (writers don't block readers in WAL mode at all), but we haven't proven it. Out of scope for the gate; would be a follow-up if we ship.

5. **The `SQLitePooled` allocation numbers include one ChunkIndexEntry list per reader thread** (~44 KB each), which is why pooled allocs grow linearly with reader count. The dominant cost is the result-set object graph — same as `GetChunkEntriesForFile` in C-3 (2/N).

## Recommendation

**This is the inflection point**. We have:
* 1 decisive pass (`GetChunkEntriesForFile`)
* 1 marginal but strictly-positive (`RebuildReverseChunkIndex`)
* 1 decisive pass (`ConcurrentReaders`)

We need **1 more decisive pass** from the 2 remaining scenarios (`Open + decrypt`, `BackedUpFile upsert`) to hit the 3-of-5 ship gate.

`Open + decrypt` is structurally LiteDB-favourable: LiteDB starts up almost instantly because its journal is small; SQLCipher has to do the Argon2id pass + page-1 HMAC validation. I expect SQLite to **lose** here, possibly by 5×+. So the realistic-pass scenario is `BackedUpFile upsert`.

**Continue with C-3 (5/N): `BackedUpFile` upsert throughput.** If that one passes decisively, we're at 3 of 5 and Option C is a ship. If not, we should `Open + decrypt` to confirm the loss and then write the decision doc with a no-ship recommendation.

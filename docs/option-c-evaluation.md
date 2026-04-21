# Option C — LiteDB → SQLite Migration Evaluation

> Status: **Draft for review**
> Branch: `feature/option-c-eval`
> Last updated: Phase 6.1 follow-up

This document evaluates replacing LiteDB with SQLite as the backing store for
`LocalDatabaseService`. It exists so we can decide *whether* to do the work
before committing to it.

## 1. Why we are considering it

Phases 4–6 surfaced the same pattern repeatedly: a measured optimisation
hit a ceiling that turned out to be inside LiteDB itself, not our code.
Concrete examples:

| Issue | Where it bit us |
|---|---|
| `IEnumerable.Contains` not translatable to BSON | Phase 5 reverse-index lookup (caused a 5-test regression in `b0c9439`) |
| `HashSet.Contains` not translatable to BSON | Phase 4 batched pending-changes had to fall back to per-path `DeleteMany` |
| Coarse internal page lock | Phase 5 RWLock benchmark capped concurrent reader scaling at ~14× of 32 threads |
| BSON deserialisation cost dominates `FindAll` | Phase 5 chunk-summary load |
| No JSON / nested-array indexing | Forced the entire reverse-index design (`chunk_file_refs` collection) |
| WAL grows unbounded between automatic checkpoints | Required discovered-#3 manual `Checkpoint()` timer |

None of these are bugs in LiteDB — they are reasonable trade-offs for a
zero-dependency embedded BSON store. They just bound how much further we can
push performance without changing the engine.

SQLite gives us:

- A real query optimiser with `IN (?, ?, ...)`, joins, subqueries, CTEs.
- Mature `WAL` journaling with auto-checkpointing and `PRAGMA wal_autocheckpoint`.
- True multi-reader concurrency under WAL (multiple read transactions never
  block each other; one writer is exclusive but read-friendly).
- `JSON1` extension for the few cases we still want document-style storage.
- Native `INDEX`-aware optimiser for compound and partial indexes.
- `PRAGMA optimize` that can rewrite query plans based on statistics.

It costs us:

- A native dependency (the SQLitePCLRaw bundle).
- A migration path for every existing user's encrypted LiteDB file.
- Schema work — explicit tables, types, foreign keys, indexes.
- Per-call serialisation overhead (Dapper / hand-written readers vs
  LiteDB's BSON deserialiser).

## 2. Decisions already made

These were settled before this document was written:

| # | Decision | Rationale |
|---:|---|---|
| 1 | **Full-file encryption is required.** | The local DB stores file paths, hashes, and Azure connection metadata; the user installed this app expecting at-rest encryption equivalent to LiteDB's current AES password protection. |
| 2 | **Background migration with progress UI** (matches Phase 5's reverse-index rebuild UX). | Existing users open the app, see "Migrating database to SQLite (one-time)…" with progress, and continue once it finishes. Same modal pattern as P3. |
| 3 | **Prototype on this branch first**, not on `main`. | Lets us discard cleanly if the prototype shows regression. Keeps `main` shippable. |

## 3. Library and encryption choice

### Recommended stack

```text
Microsoft.Data.Sqlite       10.0.6     ADO.NET API surface
SQLitePCLRaw.bundle_e_sqlcipher 2.1.11  Native SQLite + SQLCipher (BSD)
Dapper                      2.1.72     Micro-ORM (mapping only, no LINQ provider)
```

### Why these specifically

**Microsoft.Data.Sqlite**
- Ships as part of the .NET ecosystem; same release cadence as the runtime.
- Pure ADO.NET (`SqliteConnection`, `SqliteCommand`, `SqliteDataReader`).
- No LINQ provider — which means **we will never hit the `Contains` /
  `BsonExpression` translation problem** that bit us in Phase 5/6. Every
  query is a literal SQL string we control.
- Honours `bundle_e_sqlcipher` automatically via SQLitePCLRaw's bundle
  registration; no manual wiring.

**SQLitePCLRaw.bundle_e_sqlcipher**
- Statically links the open-source **SQLCipher Community Edition**
  (BSD-3-Clause). No commercial licence required for our open-source
  project.
- Provides full-file AES-256 encryption via `PRAGMA key`.
- Identical API to the standard `bundle_e_sqlite3` so swapping back is
  trivial if encryption requirements ever change.
- Native binary is platform-specific; the bundle handles x64/arm64 for
  Windows/Linux/macOS automatically.

**Dapper**
- Pure mapping helper: `connection.Query<T>(sql, params)` returns `T`.
- No expression-tree pitfalls. SQL is the source of truth.
- Battle-tested at scale (Stack Overflow runs on it).
- Optional — we could write hand-rolled readers — but Dapper saves
  ~50 LOC per repository method without adding magic.

### Encryption key derivation

We already have an Argon2id key derivation in `LocalDatabaseService.cs` for
the LiteDB password. The same derived key feeds SQLCipher via:

```csharp
using var conn = new SqliteConnection($"Data Source={path}");
conn.Open();
using var cmd = conn.CreateCommand();
// SQLCipher: pass raw key bytes as hex literal so it skips its own KDF.
// We have already done Argon2id, so we want a "raw key" PRAGMA.
cmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(derivedKey)}'\"";
cmd.ExecuteNonQuery();
```

The `x'...'` form tells SQLCipher to use the bytes verbatim instead of
running its own PBKDF2 (we already did Argon2id which is stronger; running
both wastes work and weakens nothing).

**Salt storage**: stays exactly as today — `<dbPath>.salt`. Same file,
same Argon2id parameters. The migration code reuses the file so the
user's password works unchanged.

## 4. Schema design

### Decision: pure-relational schema (locked in)

Every persisted collection becomes a real SQLite table with typed
columns and proper foreign keys. JSON columns are not used. Discussion
of the trade-off is preserved at the bottom of this section so future
readers understand why the alternative was rejected.

| Table | Purpose | Notes |
|---|---|---|
| `config` | Single-row backup configuration | All scalars on the row; lists below |
| `watched_folders` | One row per `WatchedFolder` | FK back to `config` (always row 1); `Path` `UNIQUE INDEX` |
| `watched_folder_excludes` | One row per pattern in `WatchedFolder.ExcludePatterns` / `ExcludeSubfolders` | Discriminator column distinguishes the two sources |
| `global_exclude_patterns` | One row per pattern in `BackupConfiguration.GlobalExcludePatterns` | FK to `config` |
| `files` | One row per `BackedUpFile` | `LocalPath` `UNIQUE INDEX` |
| `file_chunks` | One row per chunk in `BackedUpFile.Chunks` | FK to `files` with `ON DELETE CASCADE`; `(file_id, chunk_order)` `UNIQUE`; `chunk_order` preserves array index |
| `pending_changes` | One row per `FileChangeEvent` | `FilePath` indexed for the dedup-by-path semantics |
| `chunk_index` | One row per unique chunk | `ChunkHash` `UNIQUE INDEX`, `ReferenceCount` index for orphan scan; `ReferencingFiles` removed (replaced by `chunk_file_refs`) |
| `chunk_file_refs` | One row per (file, chunk) pair | Already normalised; `FilePath` and `ChunkHash` indexed |
| `index_metadata` | Key/value table of timestamps | Trivial |

### Why pure-relational and not hybrid (JSON columns)

The hybrid alternative was to keep `BackedUpFile.Chunks`,
`WatchedFolders`, and `GlobalExcludePatterns` as JSON columns and only
normalise the rest. We rejected this for the following reasons (in
order of importance):

1. **Self-documenting schema.** A future reader inspecting the DB sees
   the data model directly without reading the C# class. We just got
   bitten by an opaque-storage bug (the `b0c9439` `Contains`
   regression); enforcing schema visibility is a concrete safety win.
2. **Foreign-key-enforced integrity.** The DB itself prevents orphaned
   chunk rows. With JSON, a buggy update could corrupt the chunks list
   and SQLite would not notice until restore time.
3. **Tooling.** `sqlite3` CLI, DB Browser, and `EXPLAIN QUERY PLAN` all
   work naturally on real columns. JSON queries via `json_extract` /
   `json_each` are workable but cannot use covering indexes well.
4. **Future-proofing.** Queries we have not designed yet (largest chunk
   in a file, chunks at a given offset range, deduplication ratio per
   file) all become trivial SQL.

The hybrid model has two real performance advantages we accepted as
costs:

- **File rebackup.** Replacing N chunks costs `DELETE FROM file_chunks
  WHERE file_id = ?` + N inserts in a transaction, vs one `UPDATE` of a
  JSON column. Worst realistic case: a 50 GB VM image at the 64 MB
  chunk profile is ~800 chunks ≈ 16 ms at SQLite's ~50K inserts/sec
  per transaction. This is an order of magnitude faster than the
  network round-trip for one chunk upload, so the cost is invisible.
- **Per-row metadata overhead.** SQLite's per-row bookkeeping costs ~30
  bytes; for a million-chunk database that is ~30 MB of metadata that
  JSON would not pay. Acceptable on any modern disk.

### What we drop from the LiteDB schema

`ChunkIndexEntry.ReferencingFiles` becomes unused (the reverse-index has
been authoritative since Phase 5). Phase 5 / discovered-#7 already
flagged this; the SQLite migration is the natural place to delete it.

## 5. Migration path for existing users

Single sequence executed at first login post-upgrade:

1. Detect: SQLite DB does not exist, LiteDB DB does, salt file present.
2. Open LiteDB read-only with the user's password (Argon2id key derived
   from `<path>.salt`).
3. Open new SQLite DB at `<path>.sqlite` with the same derived key
   (SQLCipher) and create the schema.
4. Stream rows table by table. Use a single transaction per table; commit
   between tables so a crash mid-migration leaves a consistent partial
   state we can resume.
5. After all tables succeed: rename `<path>.sqlite` → `<path>.db` (the
   target name) and `delete` the original `<path>.db` (LiteDB file).
   Salt file stays.
6. On next launch the SQLite DB exists and the LiteDB code path is
   never touched again. **No backup file is retained** (per locked
   decision); migration safety relies on never deleting the LiteDB
   file until the SQLite write is fully committed and renamed.
   Atomicity is enforced by performing the SQLite finalise (`PRAGMA
   wal_checkpoint(TRUNCATE)`) **before** the delete.

UI: same `EnsureReverseChunkIndexBuiltAsync`-style helper. Progress is
reported per table with row counts.

Cancellation: same as the reverse-index rebuild — if the user cancels
mid-migration, delete the partial SQLite file so the next launch
restarts cleanly. The original LiteDB file is **never touched** until
the entire migration succeeds.

## 6. Performance projections

These are estimates from the Phase 5/6 benchmark numbers, not new
measurements. The prototype phase will replace them with real numbers.

| Workload | LiteDB today | SQLite estimate | Confidence |
|---|---|---|---|
| `GetChunkEntriesForFile` (500K chunks, 100 chunks/file) | 348 ms | **~30–60 ms** | High — `IN (?, ?, ...)` with indexed `ChunkHash` should be ~10× faster than 100 separate `FindOne` calls |
| Concurrent readers (16 threads) | 6,544 ms | **~1,500–3,000 ms** | Medium — SQLite WAL allows true reader parallelism but writes still serialise |
| Reverse-index rebuild (500K chunks) | 47 s | **~10–20 s** | Medium — bulk insert with prepared statements + single transaction is dramatically faster than LiteDB's `InsertBulk` |
| Database open / decrypt | ~1 s (Argon2id dominates) | ~1 s (same KDF) | High — KDF is the bottleneck on both |
| `BackedUpFile` upsert | ~1 ms | ~0.5 ms | Low — Dapper has slight overhead; SQLite has tighter indexes; net unclear |

The headline expectation: **Phase 5/6 wins compound rather than re-appear**.
The reverse-index design we built around LiteDB's expression-tree limits
becomes a much better fit for SQLite (clean `JOIN` query) but doesn't need
to change shape.

## 7. Risks

| # | Risk | Mitigation |
|---:|---|---|
| 1 | Migration corrupts a user's data | Never overwrite the LiteDB file until SQLite is fully written and renamed. Keep a `.litedb-bak` for one release. |
| 2 | SQLCipher native bundle adds platform-specific deployment quirks | The bundle handles win-x64/win-arm64/linux-x64/osx-x64/osx-arm64 transparently; verify on each target during prototype. |
| 3 | New cache of bugs in our hand-written SQL | Every existing test in `LocalDatabaseServiceTests` still runs against the new backend without modification — that's the contract. |
| 4 | Performance regression in some edge case | Benchmarks must be re-run for every Phase 5/6 scenario before merge. Roll back if any p50 regresses by >20%. |
| 5 | Increased binary size (native dll) | SQLCipher bundle adds ~3 MB per platform. Acceptable for an installable desktop app. |
| 6 | Migration takes longer than the user is willing to wait | At 100K rows/table the migration completes in <10 s. At 500K we are in the 30–60 s range — same as Phase 5's reverse-index rebuild, which we already deemed acceptable. Larger DBs need a "this may take a few minutes" message. |
| 7 | Two storage formats coexisting in the field for a release window | Acceptable trade-off for safety; handled by detection logic in `Initialize`. |

## 8. Effort estimate

These are working-day estimates assuming the prototype goes well.

| Phase | Description | Effort |
|---|---|---:|
| C-0 | This evaluation document | 0.5 d (done) |
| C-1 | Prototype: SQLite-backed `LocalDatabaseService` behind a feature flag, all 536 tests green against it | 3–4 d |
| C-2 | Migration code path with progress UI | 1–2 d |
| C-3 | Re-run every Phase 4/5/6 benchmark against SQLite, document results | 1 d |
| C-4 | Decision point: ship, defer, or abandon based on C-3 numbers | — |
| C-5 (if shipping) | Remove LiteDB code, drop `LiteDB` package, update README | 0.5 d |
| C-6 (if shipping) | Soak in main behind preview flag for one release before forced migration | — |

**Total best case: ~7 days** of focused work spread across calendar
weeks for measurement and review.

## 9. Recommendation

**Proceed to C-1 (prototype) on this branch**, with the explicit
checkpoint at C-3: if the benchmarks do not show clear wins on at least
3 of the 5 scenarios in §6, we abandon the migration and document why,
keeping LiteDB.

The prototype phase is the cheapest way to convert the open question
"is SQLite worth it for this app?" into a quantified answer. Even if we
abandon, the work is bounded: ~5 days for a written go/no-go on
infrastructure that touches the entire app.

## 10. Locked decisions (resolves §10 open questions)

| # | Decision | Rationale |
|---:|---|---|
| 1 | **Pure-relational schema** | Self-documenting, FK-enforced, future-proof for queries (see §4). |
| 2 | **Delete LiteDB file on successful migration** | Atomicity is guaranteed by writing SQLite to a temp filename and only renaming + deleting once the SQLite WAL is checkpointed (see §5). |
| 3 | **No migration telemetry** | Keep the migration code path minimal; rely on log statements to the existing diagnostic log if real-world data is ever needed. |

## 11. C-3 decision rationale — **SHIP**

This section replaces §6 (performance projections) with measured
numbers and resolves the §9 decision gate. Written at the close of
C-3, after every head-to-head benchmark in §6 had been built, run,
and in several cases re-run after code-review-driven optimisations.

### 11.1 Measured scorecard (5 scenarios)

| # | Scenario | Ratio (SQLite / LiteDB) | Speedup | Source | Verdict |
|--:|---|---:|---:|---|:---|
| 1 | `GetChunkEntriesForFile` | 0.001–0.02 | 50–1000× | C-3 (2/N), commit `c97c559` | ✅ **PASS** |
| 2 | `RebuildReverseChunkIndex` | **0.116–0.201** | **5–8.7×** | C-3 (3d), commit `4614004` | ✅ **PASS** |
| 3 | `ConcurrentReaders` (pool, 16 threads) | 0.0001 | **7 275×** | C-3 (4b), commit `577bcc0` | ✅ **PASS** |
| 4 | `BackedUpFile upsert` | 0.061–0.207 | 4.8–16.5× | C-3 (5b), commit `8aeda95` | ✅ **PASS** |
| 5 | `Open + decrypt` | 4.90 | 0.2× (slower) | C-3 (6b), commit `16e2855` | ❌ **LOSE** |

**4 of 5 decisive passes.** Gate (§9: "Ratio < 0.5 on at least 3 of 5
scenarios") is cleared with margin to spare. Scenario 5 is the only
loss and is a **one-time-per-launch cost** (~385 ms marginal over
LiteDB), not a hot-path cost.

### 11.2 Projections vs reality

The §6 projections, written before any measurement, proved
conservative on every scenario:

| Scenario | §6 projection | Measured | Off by |
|---|---|---|---|
| `GetChunkEntriesForFile` (500K/100) | ~30–60 ms | 264 μs | **100–230× better** |
| Concurrent readers (16 threads) | ~1.5–3 s | 0.80 ms | **1 875–3 750× better** |
| Reverse-index rebuild (500K) | ~10–20 s | 4.96 s | 2–4× better |
| Database open / decrypt | ~1 s | 485 ms | 2× better |
| `BackedUpFile` upsert | ~0.5 ms | 0.6–2 ms | In range |

The single biggest reframing during C-3 was **scenario 5**: the doc
assumed Argon2id was a pure SQLite tax, but the measurement showed
both backends pay Argon2id equally (LiteDB also runs Argon2id at
open; the perceived "instant" startup users feel today is actually
~100 ms). SQLCipher adds ~385 ms of marginal cost on top of the
shared Argon2id, not 300–1000 ms of net new cost.

### 11.3 Memory pressure (not in original projections)

| Scenario | LiteDB allocation | SQLite allocation | Ratio |
|---|---:|---:|---:|
| ConcurrentReaders @ 16 | 1 054 MB per iteration | 707 KB | 0.0007 |
| RebuildReverseChunkIndex @ 500K | **35 GB per iteration** | **8 KB** | 0.0000002 |
| BackedUpFile upsert @ 100 chunks | 880 KB | 168 KB | 0.191 |
| GetChunkEntriesForFile @ 500K | 66 MB per iteration | 44 KB | 0.0007 |

The ConcurrentReaders and Rebuild cases trigger **tens of thousands
of Gen 2 collections per single iteration** on LiteDB. SQLite
triggers **zero** Gen 2 collections on every scenario we measured.
This is an independent argument for SQLite beyond the raw-throughput
wins.

### 11.4 Production-relevant improvements committed during C-3

The C-3 phase produced production improvements beyond the original
C-1 scope. Enumerated for handoff to C-1 final step b:

1. **`PRAGMA cache_size = -65536`** (64 MB) in `ApplyPragmas` — C-3
   (3c-1) commit `9891bf0`. Every SQLite operation benefits.
   Scenarios 1/3/4 not re-measured with this change active; numbers
   would be equal or slightly better.

2. **`RebuildReverseChunkIndex` rewritten** from 256-file batched
   loop to single `INSERT … SELECT` with `NOT EXISTS` idempotency —
   C-3 (3c-1). Net −30 LOC, 5× faster at scale.

3. **Index drop + recreate during bulk rebuild** — C-3 (3c-2) commit
   `a90e168`. Gated by empty-table safety check so resumed rebuilds
   stay correct. Regression test added.

4. **`OpenAndUnlockCore` static helper** extracted from
   `OpenAndUnlock` — C-3 (4a) commit `163a0f4`. Lets the writer and
   any read-only callers share the exact same key-derivation path.
   This is the foundation for the future production connection pool.

5. **Explicit WAL checkpoints** after one-time migration operations
   — C-3 (3c-1) + C-3 (3c-3). Keeps next-operation latency low.

### 11.5 Benchmark-only scaffolding that stays in the benchmark project

These were built during C-3 specifically because the production
refactor was deferred behind the gate. They are **not** production
code and should not leak into `SqliteBackend`'s public API:

* `SqliteBackend.BulkInsertFilesForBenchmark` (internal)
* `SqliteBackend.ClearReverseChunkIndexForBenchmark` (internal)
* `SqliteBackend.OpenReadOnlyForBenchmark` (internal static)
* `SqlitePooledReader` class (in the benchmark project)

C-1 final step b should build the production versions with proper
rent/return semantics. The production versions should replace the
`*ForBenchmark` helpers wherever the benchmark uses them, after which
the helpers become regression targets in the benchmark project only.

### 11.6 Updated §7 risks

| # | Risk | Status after C-3 |
|--:|---|---|
| 4 | Performance regression in some edge case | **Resolved** for the 4 measured hot paths. Scenario 5 (open + decrypt) is a ~385 ms per-launch regression; mitigation is a brief spinner if smoke testing shows a visible pause. |
| 6 | Migration takes longer than the user is willing to wait | **Revised lower.** At 500K chunks the reverse-index rebuild phase alone is now 5 s (was projected 10–20 s). Total migration flow (open LiteDB + copy rows + rebuild + swap) is expected ~60–90 s for a 500K-chunk heavy user. Eval doc §2's blocking-modal-with-progress UX still correct. |
| Others | unchanged | — |

### 11.7 Updated §8 effort estimate

Working-day estimates, calibrated against C-3 actuals:

| Phase | Description | Original | **Actual / Revised** |
|---|---|---:|---:|
| C-0 | This evaluation document | 0.5 d | 0.5 d (done) |
| C-1 | Prototype backend | 3–4 d | ~6 d done (includes C-1e reverse-index + C-1f contract tests + C-1g GetStatistics + C-1-final-a LiteDbBackend adapter) |
| C-2 | Migration code path with progress UI | 1–2 d | 1–2 d (unchanged, now unblocked) |
| C-3 | Re-run every Phase 4/5/6 benchmark against SQLite | 1 d | **~5 d actual** (scaffolding + 6 benchmarks + 2 optimisation-then-rerun passes + analysis docs) |
| C-4 | Decision | — | **Done: SHIP** |
| C-5 | Remove LiteDB code + package | 0.5 d | 0.5 d (unchanged) |
| C-6 | Soak in main behind preview flag | — | — |

The C-3 overrun (1 d → 5 d) came from three things:

1. **Benchmark bugs** surfaced only after partial runs, requiring
   (3a) and (4a) fix commits. Two of the five scenarios needed a
   rewrite pass.
2. **Code-review-driven optimisation** of scenario 2 after its
   marginal result (the C-3 (3c) pass, three commits). This was
   outside the original scope but turned the one marginal result
   into a decisive pass.
3. **Honest-disclosure writeups** per scenario to keep attribution
   traceable.

None of the overrun is unrecoverable; the prototype phase itself
came in at ~6 d vs a 3–4 d estimate, so the total is ~12 d vs ~7 d.
Acceptable for a foundational infrastructure decision.

### 11.8 Final recommendation

**Ship Option C (LiteDB → SQLCipher).**

4 of 5 scenarios pass the gate decisively. The fifth (open + decrypt)
is a one-time per-launch cost of ~385 ms marginal over LiteDB — noticeable
only on cold start and addressable with a brief spinner if smoke
testing reveals a pause.

Immediate next work, in order:

1. **C-1 final step b** — the `LocalDatabaseService` refactor onto
   `IDatabaseBackend`, with a feature flag (`AZBK_USE_SQLITE` env
   var or equivalent) routing a user to either `LiteDbBackend` or
   `SqliteBackend`. Production connection pool on top of
   `OpenAndUnlockCore` is part of this step.
   **Status: landed (b-1 = `088d019`, b-2 = this commit).** The
   feature flag is documented in the root `README.md` under
   "Experimental: SQLite backend preview". The production
   connection pool was deferred - the current SqliteBackend still
   uses one connection per LocalDatabaseService instance, matching
   the C-3 (5b) measured topology. Pool lands as a separate commit
   if/when it becomes necessary.
2. **C-2** — the migration code path with blocking-modal progress UI.
   **Status: code path landed (`9fda662` + `f319782`).** The migration logic is
   complete and tested (4 integration tests covering full round-trip,
   idempotency, wrong-password safety, and a regression test for the
   AsyncLocal-recursion bug below). Detection happens in
   `LocalDatabaseService.Initialize`: when the flag is on AND the
   target file is not a SQLite database (probed via SqliteBackend
   open + InvalidPasswordException catch), we read every collection
   from LiteDB and write it into a fresh SqliteBackend at
   `<path>.sqlite-tmp`, then rename the four files into place. The
   original LiteDB is preserved at `<path>.litedb-backup`. The
   blocking-modal progress UI is **NOT** wired - migration runs
   synchronously on the calling thread. The `IProgress<>` parameter
   on `MigrateFromLiteDb` is plumbed but no caller builds a
   reporter; that lands when MainWindow grows the modal.
   Concurrent-write safety (the `SqliteBackend` write lock) was
   discovered as a prerequisite while landing this and is part of
   the same commit.

   **Why two commits:** `9fda662` shipped the migration code with a
   recursion bug — `InitializeLiteDbOnly` cleared only the env var,
   not the AsyncLocal override added in the same commit, so the
   inner `Initialize` re-evaluated `ShouldUseSqlite()` to true,
   re-detected the LiteDB file, and recursed back into
   `MigrateFromLiteDb` until the test host stack-overflowed. xUnit
   reported a misleading aggregate pass count from before the crash,
   masking the failure. `f319782` clears both switches AND adds a
   per-instance `_initializeInProgress` re-entry guard that converts
   any future regression into a fast `InvalidOperationException`
   instead of a stack overflow.
3. **C-6** — one release in main behind the preview flag before
   forced migration.
4. **Post-ship calibration re-run** (optional) — scenarios 1, 3, 4
   with the new `cache_size = 64 MB` setting active, for a fully
   symmetric decision record. Gate clears without this; not a
   blocker.


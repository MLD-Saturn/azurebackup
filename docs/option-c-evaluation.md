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

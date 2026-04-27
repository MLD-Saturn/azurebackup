# AGENT_CONTEXT.md — Handoff for Copilot Sessions

This file is the **persistent memory** for Copilot agent sessions on the AzureBackup repo. It is checked into source control so the agent can read it on a fresh chat (on **any machine, any OS-supported environment**) and immediately understand the project state, conventions, recent decisions, and open questions.

## How to use this file

**At the start of every fresh Copilot chat:** read this file before doing anything else. Treat the "Active workstreams" section as a continuation of the previous session.

### Pre-commit checklist (run this immediately before every `git commit`)

This section is the load-bearing rule of the file. **The agent has demonstrably skipped it before.** When that happens the file silently drifts and the next session inherits a lie. Treat it as non-optional.

Before running `git add .; git commit -m "..."`, walk through these questions in order. If the answer to any is "yes", the commit MUST also include an AGENT_CONTEXT.md edit (and frequently a `docs/USER_GUIDE.md` or `docs/SETUP.md` edit per the Documentation trust policy).

1. **Did the change introduce a new architectural fact?** New `public` API on a `Core` type, new cross-project constant, new env var read at startup, new build-time `DefineConstants`, new file-format magic number / version byte, new partial-class seam, new singleton, new background task, new on-disk file location, new lock or shared-state contract. → Add an entry to the "Architectural facts to load into working memory" list. Do not assume "it's just a refactor" — a public API / public constant / cross-boundary contract IS a fact.
2. **Did the change introduce or remove a hard-coded duplicate of a value used in more than one project?** → Add a fact recording the canonical location and a "do not introduce a new copy" warning. (See fact #9 for the template.)
3. **Did the change land a commit with a B-number?** → Add a row to the "Recent commit history" table. Add an entry to "Completed workstreams". If the commit consumed a B-number that was pre-allocated to a different planned task in "Active workstreams", renumber the planned task and add a one-line note explaining the renumbering.
4. **Did the change resolve, block, or pivot any workstream?** → Update the corresponding W-numbered section. If a workstream is now done, move it to "Completed workstreams".
5. **Did the change establish or change a user preference, coding convention, or workflow rule?** → Add or update the corresponding entry under "Hard rules".
6. **Did the change discover a non-obvious production behavior** (memory, concurrency, persistence, network, encryption)? → Add a fact.
7. **Did the change produce a benchmark number that affects a design decision?** → Update the relevant benchmark file's xmldoc table (the source of truth) AND add a one-line summary in the relevant W-numbered section. Do not paste the full numbers here.
8. **Did the change teach a "do not do this" lesson?** → Add a fact, with the word "NEVER" or "DO NOT" so a future session greps for it.
9. **Did this session touch AGENT_CONTEXT.md at all?** → Add a dated bullet to the "Maintenance log for THIS file" at the bottom summarizing what changed in the file. One bullet per session, not per edit.

If the answer to all nine is "no", commit without touching this file. That is the only acceptable reason to skip the update.

### Hidden failure mode that has happened before

The trap is reasoning "this commit only changes code, it doesn't change documentation, so AGENT_CONTEXT doesn't need an update." That logic is wrong because **AGENT_CONTEXT documents the code, not the documentation**. A code commit that adds a new public constant is exactly the trigger for question 1 above. Commit `b9d5744` (B26) hit this trap; the catch-up was commit `df0df53` and cost the user an extra prompt to ask "did you remember the file?". Do not repeat it.

### Edit policy (style)

- Keep entries dated (YYYY-MM-DD).
- Move completed workstreams to the "Completed workstreams" section near the bottom.
- Keep the file under ~600 lines so a fresh agent can read it in one tool call.
- **Never** record machine-specific values (free disk, CPU model, core count, terminal-tool timeouts, exact wall-clock times, OS-edition build numbers). Those mislead the next session if it is on a different PC. Record *relative* findings (deltas, percentages, comparisons) and *workload* identity (which benchmark / which workload name produced the number) instead.

**Anti-pattern:** do not duplicate code or full xmldoc into this file. Reference file paths, commit hashes, and benchmark file xmldoc tables instead.

---

## Project at a glance

- **Name:** AzureBackup — a cross-platform desktop application that backs up local files to Azure Blob Storage with client-side encryption and content-defined chunking for deduplication. Runs on Windows, macOS, and Linux.
- **Language/runtime:** C# on .NET 10 (`net10.0` TFM).
- **Solution:** `azurebackup.sln` at repo root.
- **Project layout:**
  - `src/AzureBackup.Core/` — all backup/restore/encryption/database logic. Cross-platform-clean C# library; no UI dependencies. **All non-trivial logic lives here.**
  - `src/AzureBackup/` — **Avalonia 11.3.12** desktop UI shell (`MainWindowViewModel` + views). Targets `net10.0` (no `-windows` suffix), so it builds and runs on Windows, macOS, and Linux. Verify with `Select-String Avalonia src/AzureBackup/AzureBackup.csproj` if in doubt.
  - `tests/AzureBackup.Tests/` — xUnit. As of B25-bench-2: 759 tests, all passing.
  - `benchmarks/AzureBackup.Benchmarks/` — BenchmarkDotNet 0.15.8. Release-only, never run in CI. See "Running tests and benchmarks" below.
- **Setup, build, publish, file locations:** `docs/SETUP.md` is current and authoritative as of the most recent commit that touched it. If you change build flags, target frameworks, runtime identifiers, the `portable.marker` mechanism, the `AppMode.DataDirectory` resolution, or the encryption/chunking parameters listed in its "Technical specifications" table, you MUST update `docs/SETUP.md` in the same commit.
- **End-user UX (views, settings, menus, dialogs):** `docs/USER_GUIDE.md` is current and authoritative as of the most recent commit that touched it. If you add or rename a view (`*.axaml` under `src/AzureBackup/Views/`), add or rename a button/toggle/setting, change a default value visible to the user, or change a user-visible workflow, you MUST update `docs/USER_GUIDE.md` in the same commit.
- **Database backend:** SQLCipher-encrypted SQLite (production default since commit `63103b1` = `C-5`). Legacy LiteDB code path still present for migration only.
- **Authentication:** Argon2id KDF (64 MB, 8 lanes, 3 iterations) for both database key derivation and the `EncryptionService` content key.
- **Encryption envelope:** AES-256-GCM with `[magic(4) | version(1) | nonce(12) | ciphertext(N) | tag(16) | crc32(4)]`. `EncryptionService.EncryptionOverhead = 37`.
- **Chunking:** content-defined (Rabin-style rolling hash, window=48, prime=31). Per-extension chunk-size config (`.mkv` uses 1-64 MB, `.txt` uses 16-128 KB, etc.). Files >500 MB use 16-128 MB chunks regardless of extension.

---

## Hard rules (USER PREFERENCES — never violate)

These come from `.github/copilot-instructions.md` and from explicit user instruction during session work. Reproduced here because they apply to every session.

### Documentation trust policy

**Treat every Markdown documentation file in this repo as stale until proven current** — with two specific exceptions noted below. This policy covers any `README.md`, `AGENT_CONTEXT.md` (yes, even this file — verify its claims before relying on them), anything under `docs/` such as `stress-test-plan.md` and the `option-c-c3-results-*.md` series, and any `CONTRIBUTING.md` / similar that may appear later.

**Verified-current docs (treat as authoritative, but maintain rigorously):**

- `docs/SETUP.md` — verified accurate against source on 2026-04-24. Covers Azure provisioning, build-from-source, single-file publish, portable mode (`portable.marker`), encryption envelope, per-extension chunking config, storage tiers, `AppMode.DataDirectory` paths, and a verified "Technical specifications" table.
- `docs/USER_GUIDE.md` — verified accurate against source on 2026-04-24. Covers every view (`SyncView`, `SettingsView`, `StorageHealthView`, `TierMigrationView`, `DataIntegrityView`, `LogsView`), the diagnostic-bundle export flow, the memory-limit slider, and all four storage tiers (Hot/Cool/Cold/Archive).

**Mandatory maintenance protocol for the verified-current docs:** every commit that touches one of the things they describe MUST update them in the same commit. Specifically:

- Adding/renaming a view file (`src/AzureBackup/Views/*.axaml`) → update `docs/USER_GUIDE.md` table of contents and the corresponding section.
- Adding/renaming a user-visible button, toggle, or setting → update `docs/USER_GUIDE.md`.
- Changing a default value the user can see (default storage tier, default container name, default exclusion patterns, default memory-limit state) → update `docs/USER_GUIDE.md`.
- Changing the encryption envelope, Argon2id parameters, chunk-size config, storage-tier set, or `AppMode.DataDirectory` resolution → update `docs/SETUP.md`.
- Changing build flags, target framework, runtime identifiers, the publish profile, or NuGet package versions called out in the technical-specifications table → update `docs/SETUP.md`.
- Adding or renaming a top-level menu, tab, or workflow step → update both docs as appropriate.

When the doc and the code drift, the doc is wrong by definition; fix the doc, do not weaken the code to match. If a commit cannot reasonably update the doc in the same change (e.g. an emergency hotfix), record an observation in this file's maintenance log and open a follow-up.

For any other Markdown doc not on the verified-current list:

- Verify it against the actual code, project files, configuration, build output, or a fresh test run. Do not paraphrase a doc claim into a response without that verification.
- If the statement is wrong or out of date, update the doc file in the same commit as the code change that reveals the discrepancy. Do not leave a known-wrong doc file in place.

This file (`AGENT_CONTEXT.md`) is the one doc that future sessions are *required* to read first, so the bar for keeping it current is correspondingly higher: every commit that invalidates anything in it MUST update it in the same commit, and the maintenance log at the bottom must record the change.

### Terminal commands

- **Single physical line, always.** Chain with `;`. No here-strings, no backticks, no multi-line.
- The user's shell is **PowerShell 7+ (`pwsh.exe`)**. Other shells will be flagged immediately.
- Length is not a concern — correctness and single-line execution are.
- Occasionally a tooling display artifact mangles the leading characters of the command in the echoed output (e.g. `Set-Location` shown as `t-Location`). The command **still runs correctly**; check the actual output / git state, not the echo.

### Git commits

- Commit immediately at logical stopping points.
- Use `git add .; git commit -m "..."` chained on one line, with multiple `-m` flags (one per paragraph). **Never** write the message to a file. **Never** use `git commit -F`.
- **No emoji, no non-ASCII** in commit messages.
- Escape `"` and `\` properly so the command parses cleanly.
- Commit message convention in this repo: `B<number>: <imperative summary>` (or the older `C-<number>:`). Pick the next free B-number — see `git log --oneline | Select-Object -First 1`.
- **Never run `git push` under any circumstances.** Pushing is a manual step the user performs. Leave commits local; do not invoke `git push`, `git push origin <branch>`, or any equivalent. If the user is about to switch machines, remind them to push manually rather than doing it for them.

### Tool usage

- Prefer `replace_string_in_file` / `multi_replace_string_in_file` over re-creating files. Re-create only when the file is small or being rewritten end-to-end.
- Read files before editing them.
- The `nuget_get-nuget-solver*` and `start_modernization` tools are NEVER appropriate for this project's typical work — do not invoke them unless the user explicitly asks for a package upgrade or .NET version change.
- Do not call `code_search` in parallel; do not call `run_command_in_terminal` in parallel.
- Long-running operations (benchmarks, large test sweeps) may exceed the terminal tool's timeout (whatever it is in the current environment). BenchmarkDotNet writes its result artifacts on each child-process completion, so even when the orchestrating call appears to time out you can usually find the artifacts under `BenchmarkDotNet.Artifacts/results/`. Always check there before assuming a benchmark didn't run.

### Same-hardware benchmark discipline (added in the B27 session)

- **Never compare a benchmark number from one PC against a benchmark number from a different PC.** Absolute timings (BDN `Mean`), peak working set, and SQLite write-lock contention all vary materially with CPU model, core count, RAM, disk, and OS scheduler. Cross-PC comparison is the single biggest way to draw a wrong conclusion from these benchmarks.
- The four pre-B27 benchmark classes (`BackupThroughputBenchmark`, `TwoTierFileSplitBenchmark`, `LargestFirstSchedulingBenchmark`, `AdaptiveChunkConcurrencyBenchmark`) have result tables embedded in their xmldoc that were captured on an Intel i7-9700K. Treat those numbers as **historical context from different hardware, directionally informative only**. Do not quantitatively compare them against numbers captured on any other machine.
- When making a recommendation that depends on a benchmark delta (e.g. "ship 16-way file concurrency"), make sure the baseline AND the candidate were measured **in the same BDN session on the same machine**. The B27 design-decision benchmarks all enforce this by putting the baseline (today's production default) and the candidate values in the same `[Params]` matrix so BDN runs them back-to-back.
- If the agent or the user wants to re-run a pre-B27 benchmark to compare against a B27 number, the rule is: re-run the pre-B27 benchmark in the same session as the B27 benchmark, and compare the two fresh sets. The xmldoc table from the i7-9700K is not a substitute.

---

## Architectural facts to load into working memory

These are non-obvious and have caused real bugs when forgotten.

1. **`SqliteBackend` is single-connection, single-threaded.** Post-B23 every read AND write goes through one `SQLiteConnection` serialized by a single `_writeLock`. Adding more file-level parallelism cannot help SqliteBackend throughput; it just adds contention. This is a known constraint of the chosen backend, not a bug.

2. **`FileOperationDiagnostics` (`.diag` files)** are written by 4 producers: `BackupOrchestrator.BackupFileAsync`, `RestoreService.RestoreFileAsync`, `RestoreService.Batch.cs` (CorruptedRecovery), `IntegrityCheckService.CheckOneFileAsync`. Post-B23 + B24, **all four only emit on real errors**. The `FlushAllLive` shutdown hook in `FileOperationDiagnostics` snapshots any diag still in the live `_live` registry at process exit — so the success/cancel paths in producers MUST call `Discard()` to deregister.

3. **`MemoryBudget` is a soft cap, configured via the WPF app's memory-limit slider** (`MemoryLimitEnabled` + `MemoryLimitMB` in the Configuration table). When `MemoryLimitEnabled=false` the budget is unlimited and the orchestrator can hold many GB of in-flight chunk buffers — confirmed by production telemetry showing multi-GB process working sets on large workloads with `memoryBudget=unlimited`. The dominant memory cost is `EffectiveMaxParallelChunkUploads (default 6) × per-chunk size (up to 64 MB for media) × EffectiveMaxParallelFileBackups (default 8)`.

4. **CPU is NOT the production bottleneck.** Production telemetry on a 7,313-file backup showed CPU usage well below 5%. The actual bottleneck is somewhere downstream: disk read throughput, the SqliteBackend write lock, or HttpClient/TLS state. Any "make this more parallel" hypothesis must be measured rather than reasoned about — the benchmarks under `benchmarks/AzureBackup.Benchmarks/` exist precisely for this.

5. **`InMemoryBlobService`** (`src/AzureBackup.Core/Services/InMemoryBlobService.cs`) is a production-quality fake of `IBlobStorageService`. Used by ~10 test files and by all 4 backup benchmarks. Constructor takes optional `simulatedLatencyMs` and `failureRate`. Honors deduplication. Tracks `TotalBytesUploaded` / `TotalOperations`. **This is the single biggest enabler of network-free testing in the project.** Use it instead of inventing a new mock.

6. **`AZBK_USE_SQLITE=1` env var** forces the SQLite backend. Set by benchmark `[GlobalSetup]`. Read once at `LocalDatabaseService.Initialize`; flipping mid-process is a no-op.

7. **`BackupOrchestrator` constructor** signature: `(LocalDatabaseService, EncryptionService, ChunkingService, IBlobStorageService, FileWatcherService)`. The `FileWatcherService` is required even when no folders are watched — it can be constructed without ever being started.

8. **`BackupOrchestrator` parallelism overrides (B25-bench-2 seam):** two optional `int?` properties — `MaxParallelChunkUploadsOverride` (default null → 6) and `MaxParallelFileBackupsOverride` (default null → 8). Read once per backup operation via internal `EffectiveMax*` getters so they cannot change mid-pipeline. Production behavior is unchanged when overrides are unset; the seam exists for the parallelism benchmarks. If you want to change the production *default*, change the constants in `BackupOrchestrator.cs`, not the override.

9. **`RestoreService.SmallFileThresholdBytes` (B26) is the single source of truth for what "small" means.** This `public const long` (currently `16L * 1024 * 1024`, i.e. 16 MB) lives on the `RestoreService` partial in `src/AzureBackup.Core/Services/RestoreService.Batch.cs` and is referenced by **both** the production restore pipeline (which routes files at or below the threshold through a 32-way parallel small-file lane) **and** the desktop UI (which groups the same set of files into the aggregate "Small files" progress row in `ProgressTabViewModel.SmallFileGroupText`, plus three call sites in `MainWindowViewModel.Commands.Backup.cs` / `MainWindowViewModel.Commands.TreeView.cs`). Before B26 the UI hard-coded 100 MB in three places while production used 16 MB, so the progress row mis-grouped a strictly larger set of files than the pipeline actually optimized for. **Do not introduce a new hard-coded copy of this value anywhere.** If you need the threshold in a new file, add `using AzureBackup.Core.Services;` and reference `RestoreService.SmallFileThresholdBytes` directly. The displayed user-facing label interpolates the constant so it tracks any future change automatically.

10. **B27 benchmark seam (`BackupBenchmarkBase`) — three new public surfaces.** `BackupBenchmarkBase.MemoryLimitMBOverride` is a `protected virtual int?` returning `null` by default; when non-null, `BaseIterationSetup` writes `MemoryLimitEnabled=true` + `MemoryLimitMB=<value>` via `LocalDatabaseService.SaveConfiguration` BEFORE the orchestrator runs, and `BackupOrchestrator.BackupFilesCoreAsync` picks it up via its `GetConfiguration()` call at the top of every backup. This is how the big-scale benchmarks simulate the recommended 16 GB production setting without touching production code. `BackupBenchmarkBase.BigWorkloads` is the public set of disk-heavy workloads (`media-library-500` ~260 GB, `production-scale-3000` ~70 GB, `huge-outlier-mixed` ~32 GB) gated by a new disk-space preflight at the top of `BaseGlobalSetup` that throws a clear error if free disk < 1.5x estimated workload size. The preflight reads `EstimateWorkloadBytes(Workload)` which returns 0 for the pre-B27 small workloads (no preflight) and a conservative overestimate for big ones. **Pre-B27 small workloads are unchanged** so the historical i7-9700K result tables in the four pre-B27 benchmark xmldocs remain directly comparable to a fresh small-workload run on the same hardware. Do not duplicate the `MemoryLimitMB=16384` value — if the recommended production setting changes, change it once in the relevant `*BigScaleBenchmark.cs` files (and `ProductionScaleBackupBenchmark.cs` and `MemoryBudgetBenchmark.cs`) and update this fact. **2026-04-27 correction (post-B27 hardware run):** the seam works as designed; the OOM observed at `[ChunkConcurrency=24, Workload=media-library-500]` on the first B27 hardware run was NOT caused by `MemoryLimitMB=16384` failing to bind. Root cause was the destination side -- see fact #11.

11. **`InMemoryBlobService` discard mode (B27).** New constructor parameter `bool retainPayloads = true` (default preserves all pre-B27 behaviour). When `false`, `UploadChunkAsync` / `UploadChunkDirectAsync` still record the blob name and tier in `_blobs` / `_blobTiers` (so dedup, listing, and `BlobExistsAsync` work) but the encrypted payload is replaced with `Array.Empty<byte>()`; the would-be storage cost is recorded in `_discardModeStorageBytes` so `TotalStorageUsed` stays accurate. Discard mode short-circuits the dedup-collision verification path because the original bytes are no longer present to compare against -- it trusts the hash, matching real Azure's optimistic dedup. `DownloadChunkAsync` and `VerifyChunkIntegrityAsync` are not supported against a discard-mode store, so restore-style benchmarks must NOT opt in. Production code never sets the flag; production tests never set the flag. The flag exists exclusively to remove the RAM ceiling on big-scale backup-throughput benchmarks: without it, `media-library-500` (~260 GB) cannot fit its encrypted ciphertext in process memory regardless of `MemoryBudget`, and the previously observed 58 GB peak working set was the `_blobs` dictionary growing unboundedly, not the orchestrator chunk-buffer pool. The benchmark opt-in is the new `protected virtual bool BackupBenchmarkBase.RetainBlobPayloads => true`; all five big-scale benchmark classes (`AdaptiveChunkConcurrencyBigScaleBenchmark`, `TwoTierFileSplitBigScaleBenchmark`, `LargestFirstSchedulingBigScaleBenchmark`, `ProductionScaleBackupBenchmark`, `MemoryBudgetBenchmark`) override it to `false`. **DO NOT add a new big-scale benchmark against `media-library-500` or larger without setting `RetainBlobPayloads => false`** -- it will OOM the host. **DO NOT use discard mode for any test that calls `DownloadChunkAsync` / `VerifyChunkIntegrityAsync` against the same store** -- they will fail because the bytes are gone.

---

## Active workstreams (as of 2026-04-27)

None. W1 and W2 were resolved by the B27 commit (see Completed workstreams below). The next session should pick up from a clean slate.

Follow-ups deferred from B27 (each can become its own future workstream when prioritized):

- **Workload-aware LPT scheduler.** The B27 hardware run found LPT regresses mid-sized realistic workloads (+19% on `mixed-realistic-1000`, +24% on `realistic-large-200`) but is roughly flat on big-scale realistic workloads. A scheduler that applies LPT only when size variance exceeds a threshold could in principle recover the large-skew win without the mid-sized regression, but the right shape of "variance threshold" is not obvious from the current data. Would need many more synthetic profiles between `mixed-realistic-1000` (LPT loses by 19%) and `huge-outlier-mixed` (LPT flat) to find the inflection point. Out of scope for B27 because the simpler answer (do nothing) is defensible.
- **Per-file adaptive chunk concurrency in backup.** Restore already does this (`RestoreService.ComputeAdaptiveChunkConcurrency`). For backup, the pre-B27 i7-9700K data was directionally suggestive but compromised; the post-B27 fresh data on this hardware (`AdaptiveChunkConcurrencyBenchmark` and the partial `*BigScaleBenchmark` numbers in the 2026-04-24 log) showed no monotone benefit across 4 / 6 / 12 / 24 chunk concurrency, so there is no signal for the production change to track. Re-evaluate if a future workload-shape change (e.g. dramatically larger file sizes, much higher dedup) makes the per-file chunk pipeline a measurable bottleneck.
- **`LargestFirstSchedulingBigScaleBenchmark.media-library-500` re-run.** The B27 row has a 280-minute Mean with StdDev 389 m on N=2 (one normal ~5 m run, one pathological ~9 h run during the 42-hour overnight). Almost certainly host-OS pressure rather than orchestrator behaviour; the LPT iteration on the same workload was a stable ~5 m. If a defensible number is ever needed for that cell, re-run it alone with `iterationCount>=5` in a clean session. The xmldoc result table flags this row with an asterisk and notes the cause.
- **Disk-backed test blob service.** Currently `InMemoryBlobService.retainPayloads=false` is the only escape from the 64 GB-RAM ceiling on big-scale benchmarks. A disk-backed `IBlobStorageService` would let us measure restore throughput and integrity-check throughput against a populated store at production scale, but is out of scope for B27 because backup throughput (the W1/W2 question) does not need it. See discussion in the AGENT_CONTEXT maintenance log entry below if the trade-offs ever come up again.

---

## Completed workstreams (recent)

- **B27 (commit `fee6622`, 2026-04-27)**: Re-baselined every backup benchmark on this AMD EPYC 7763 hardware after the 2026-04-25 overnight run completed cleanly, then shipped two production changes the data justified and explicitly retired two more that the data refuted. Production changes: (1) `BackupOrchestrator.MaxParallelFileBackups` raised from 8 to 16 -- `TwoTierFileSplitBigScaleBenchmark` showed -22% on `production-scale-3000` and -27% on `media-library-500` against the pre-B27 default, with the 32-way row flat against 16-way confirming 16 is the crossover. (2) `Configuration.MemoryLimitEnabled` default flipped from `false` to `true`, and `Configuration.MemoryLimitMB` default raised from 2048 to 8192 -- `MemoryBudgetBenchmark` showed all four budget values within 4.5% of each other on `media-library-500` (the worst-case workload), so constraining is free; 8 GB is the smallest stepped value that fits the new 16-way ceiling without forcing stalls. Decisions retired: (3) **B27b (per-file adaptive chunk concurrency in backup) NOT shipped** -- `AdaptiveChunkConcurrencyBenchmark` and the partial big-scale data showed no monotone benefit across 4 / 6 / 12 / 24 chunk concurrency, so there's no signal for the production change to track. (4) **B27c (blanket largest-first sort) NOT shipped** -- `LargestFirstSchedulingBenchmark` reproduced the pre-B27 regressions of +19% on `mixed-realistic-1000` and +24% on `realistic-large-200` on this hardware, and the `BigScale` companion was flat at production scale, so blanket LPT remains DO-NOT-DO. Infrastructure: a new architectural seam (`InMemoryBlobService(retainPayloads:)` constructor parameter + `BackupBenchmarkBase.RetainBlobPayloads` virtual hook) was needed to remove the RAM ceiling that was causing the first B27 hardware run to OOM at the 260 GB `media-library-500` workload. All five big-scale benchmark classes opt into `RetainBlobPayloads => false`; production tests are unchanged. See architectural fact #11 for the discard-mode contract. Result tables filled in all 7 affected benchmark xmldocs; `docs/SETUP.md` updated with the new defaults and concurrency constants; `docs/USER_GUIDE.md` updated with the new memory-limit default text. 766 of 766 tests pass (was 759 in pre-B27 AGENT_CONTEXT, +6 new discard-mode tests on `BlobServiceTests`, +1 from a separate small change already in the working tree at the time of the B27 baseline).
- **B26 (commit `b9d5744`, 2026-04-24)**: Unified the small-file threshold across the UI/Core boundary. Promoted the previously private `RestoreService.Batch.cs` constant `SmallFileThreshold = 16L * 1024 * 1024` to a `public const long RestoreService.SmallFileThresholdBytes`. Replaced three independent UI-side hard-codes of `100L * 1024 * 1024` (one in `MainWindowViewModel.Commands.Backup.cs`, two in `MainWindowViewModel.Commands.TreeView.cs`) and the user-visible string `"Small files (≤100 MB)"` in `ProgressTabViewModel.SmallFileGroupText` with references to the public constant. ProgressView now correctly groups files ≤ 16 MB into the "Small files" aggregate row, matching what the production restore pipeline actually treats as small. All 759 tests still pass.
- **B25-bench-2 (commit `171f07e`, 2026-04-24)**: Added `BackupBenchmarkBase`, `LargestFirstSchedulingBenchmark`, `TwoTierFileSplitBenchmark`, `AdaptiveChunkConcurrencyBenchmark`. Added strictly-additive `MaxParallelChunkUploadsOverride` / `MaxParallelFileBackupsOverride` seams on `BackupOrchestrator`. All 759 tests still pass.
- **B25-bench (commit `614825a`)**: First end-to-end backup throughput benchmark. Established the `InMemoryBlobService`-based pipeline test pattern.

---

## Conventions / patterns observed in this codebase

- **Numbered fixes**: every commit message starts with `B<n>:`. The current series is at **B28** (the startup-sync left-pane Avalonia TreeView Reset-event fix). Pick the next free number (B29 at time of writing) when committing a new fix or feature. Use `git log --oneline | Select-Object -First 1` to confirm the current head before picking a number.
- **`[Conditional("DIAGNOSTICLOG")]` logging**: services use `Log(string)` methods that are completely no-ops in Release without the `DIAGNOSTICLOG` constant set. The B16 commit also adds a title-bar marker showing whether the build has DEBUG/DIAG on.
- **Diagnostic events forwarded by name**: services raise `EventHandler<string>? DiagnosticLog` events; `MainWindowViewModel.OnDiagnosticLog` aggregates them all.
- **Crash-safe atomic operations**: file moves during DB migration use a sentinel `.upgrade-pending` JSON file plus 4-step rename dance. See `LocalDatabaseService.Migration.cs` and `.Migration.Sqlite.cs`. Do not invent new file-rename code without reading those.
- **`PaddedLong` for hot atomic counters**: cache-line-padded structs prevent false sharing. See `BackupOrchestrator` line ~592 for example. If adding new hot counters under parallel workers, use `PaddedLong`.
- **`ArrayPool<byte>` discipline**: producer/consumer code transfers ownership of pooled buffers via the channel. The `ChunkPayload` record carries the rented buffer and the consumer is responsible for `ArrayPool<byte>.Shared.Return(payload.Data)` after upload completes. Do not double-return.
- **No emoji** anywhere in the codebase or commit messages.
- **xmldoc style is heavy and discursive** — multi-paragraph `<remarks>` blocks explaining WHY a design decision was made, with B-numbers cross-referencing commits. Match this style when adding new public surface.

---

## Running tests and benchmarks

### Tests

`dotnet test tests/AzureBackup.Tests/AzureBackup.Tests.csproj -c Debug` from the repo root. All 759 tests pass deterministically.

If running from inside Visual Studio (which is the typical user environment), the `run_tests` / `get_tests` MCP tools work directly against Test Explorer. Use `Project=AzureBackup.Tests` as the filter.

### Benchmarks

```
dotnet build benchmarks/AzureBackup.Benchmarks/AzureBackup.Benchmarks.csproj -c Release
dotnet run -c Release --no-build --project benchmarks/AzureBackup.Benchmarks -- --filter "AzureBackup.Benchmarks.<ClassName>.*"
```

Each benchmark generates a synthetic workload on disk under the OS temp directory (path of the form `%TEMP%/azbk-backup-bench-<guid>/`) then runs the backup pipeline against `InMemoryBlobService`. The base class `BackupBenchmarkBase` cleans up stale `azbk-backup-bench-*` directories at the top of `[GlobalSetup]` so a previous run's leak (e.g. from a disk-full crash) does not poison the next run.

Results land under `BenchmarkDotNet.Artifacts/results/<BenchmarkClassName>-report-github.md` (markdown table) and `.csv` / `.html` siblings. Per-iteration peak working-set is emitted to the BDN console log under `BenchmarkDotNet.Artifacts/*.log` with the prefix `[peakWS workload=...]` because BDN's built-in `MemoryDiagnoser` reports cumulative allocated bytes, not peak resident.

Notes:
- BenchmarkDotNet 0.15.8's `-p Workload=<value>` filter is **silently ignored** — the full param matrix runs regardless. Plan benchmark cost as if every (param × iteration) combo will execute.
- Disk usage scales with workload size. Check the workload definitions in `BackupBenchmarkBase.cs` and lower the file-count or per-file-size caps locally before running on a constrained machine. The `realistic-large-200` workload is the largest of the standard set.
- Workloads are deterministic (`Random` seeded with `42`) so re-running on different hardware produces identical input bytes. **Absolute timings AND deltas between configurations both vary with hardware**; do not compare numbers from different machines (see "Same-hardware benchmark discipline" under Hard Rules above). The deterministic seeding exists to make a single machine's runs reproducible across re-runs, not to license cross-PC comparison.

---

## Open questions / things future sessions might ask

- "Why didn't largest-first work like theory said it would?" → the W1/B27 work folds this in via `LargestFirstSchedulingBigScaleBenchmark.huge-outlier-mixed`. The pre-B27 i7-9700K result is informative but not quantitatively comparable to a fresh run on different hardware; treat it as a hypothesis to test, not a finding.
- "Should we change the production default to 16-way file concurrency?" → the pre-B27 i7-9700K data said yes, but per the same-hardware discipline rule that recommendation must be re-validated by `TwoTierFileSplitBigScaleBenchmark` on the actual deployment hardware before B27a ships.
- "Should we implement adaptive chunk concurrency for backup like restore has?" → the pre-B27 i7-9700K data was directionally suggestive but compromised because `realistic-large-200` files were capped at 100 MB (only ~2 chunks per file). `AdaptiveChunkConcurrencyBigScaleBenchmark.media-library-500` is the test that actually exercises the regime this knob was designed for; B27b is gated on that result.
- "Can I re-run the benchmarks on this machine and compare?" → yes, results are under `BenchmarkDotNet.Artifacts/results/`. Comparing your new run against the xmldoc tables embedded in the pre-B27 benchmark files is a CROSS-PC COMPARISON and is forbidden by the same-hardware discipline rule. To compare a candidate configuration against today's production default, run them BOTH in the same BDN session (the design-decision benchmarks are structured to do this automatically via `[Params]`).
- "Why does the WPF project fail to build on this machine?" → trick question. The UI is **Avalonia 11.3.12**, not WPF, and it targets bare `net10.0` so it builds on Windows, macOS, and Linux. If you saw an older AGENT_CONTEXT version claim WPF / Windows-only, that was wrong and is now corrected.
- "What does `MemoryLimitMB` map to in the UI?" → a toggle plus stepped slider in `SettingsView.axaml` (search for `MemoryLimitEnabled` / `MemoryLimitSliderIndex`). Soft cap on in-flight chunk buffers; **enabled by default at 8 GB** for fresh installs (changed in B27; pre-B27 default was off / 2 GB; existing user databases keep whatever value they already had because schema column DEFAULTs only apply on row insert). See `docs/USER_GUIDE.md` "Memory limit" — verified accurate.

---

## Recent commit history (for fast catch-up)

| Hash | Message |
|---|---|
| `<B28_HASH>` | B28: fix empty Local pane on startup; document Avalonia TreeView+Reset-event footgun |
| `4364faf` | B27: fix AGENT_CONTEXT self-reference of B27 commit hash |
| `fee6622` | B27: 16-way file concurrency, MemoryLimit defaults on/8 GB, InMemoryBlobService discard mode, big-scale benchmark result tables |
| `b9d5744` | B26: UI small-file threshold reads RestoreService constant; remove 100 MB hard-code |
| `fe620d6` | docs: rewrite SETUP and USER_GUIDE against verified source, fix AGENT_CONTEXT errors |
| `a36db75` | AGENT_CONTEXT and copilot-instructions: never push, treat docs as stale until verified |
| `68410b5` | AGENT_CONTEXT: remove PC-specific data and stale workstream status |
| `171f07e` | B25-bench-2: design-decision benchmarks for backup parallelism plus AGENT_CONTEXT handoff |
| `614825a` | B25-bench: add BackupThroughputBenchmark for end-to-end backup pipeline |
| `e7a1ab1` | B24: discard restore diag on success and on cancellation |
| `31f467b` | B23: serialize all SqliteBackend reads against the shared connection; fix diag noise from successful and cancelled file backups |
| `5dfa40a` | B22: split SqliteBackend.cs into 11 partial files and gate diag logging on DIAGNOSTICLOG |
| `c0c33fc` | B21: add permanent Copilot instructions for single-line PowerShell and git commit policy |
| `0ecd72f` | B20: fix Check Now / Cancel / Re-check / Backfill buttons stuck at initial enabled state |
| `3b4a64f` | B19: stable per-file fileIndex in parallel backup progress; fix Active Files panel + small-file completion counts |

Run `git log --oneline | Select-Object -First 20` for the latest. Earlier history (157 commits total as of this writing) includes a `C-`series and an unnumbered prefix. The four most recent commits in the table above are local-only as of this writing — the user pushes manually, the agent does not.

---

## Maintenance log for THIS file

- **2026-04-24** — Initial creation. Captures B25-bench-2 in-progress state, all four benchmarks measured, two production seams added, working-tree dirty pending commit. Author: Copilot agent session (Claude Sonnet 4.5).
- **2026-04-24** — User pointed out that PC-specific data (CPU model, free disk, terminal-tool timeout, hardware-tied benchmark numbers, workstream status that referred to uncommitted edits already long-since committed as `171f07e`) misleads sessions on other machines. Removed all such data. Removed the entire stale "what remains in W1" subsection. Added explicit "never record machine-specific values" rule to the edit policy at the top. Added pointers to `docs/SETUP.md` and `docs/USER_GUIDE.md` in the project-at-a-glance section. Replaced the hardware-tied "Local environment quirks" section with a hardware-neutral "Running tests and benchmarks" section so a fresh agent on a fresh clone knows the entry-point commands. Added an explicit Windows-only note about the WPF project. Added architectural fact #8 covering the new B25-bench-2 override seam on `BackupOrchestrator`. Promoted B25-bench / B25-bench-2 into a "Completed workstreams" section. Renumbered the active W2 (LPT investigation) since the old W2/W3 collapsed. Updated the recent-commit-history table to include `171f07e`. Author: Copilot agent session.
- **2026-04-24** — Added two permanent rules: (1) never run `git push` under any circumstances, pushing is the user's manual job; (2) treat every Markdown doc in the repo as stale until verified against code, and update affected docs in the same commit as the code change that invalidates them. Added a new "Documentation trust policy" subsection under "Hard rules" capturing the second rule explicitly. Reworded the previous (also added today) `docs/SETUP.md` and `docs/USER_GUIDE.md` pointers in the project-at-a-glance section so they no longer treat those files as authoritative; instead they tell the agent to run a real `dotnet build` for setup and read the actual view code for UX questions. Reworded the corresponding `MemoryLimitMB` open-question entry the same way. Same updates applied to `.github/copilot-instructions.md` so a fresh agent reads them before even reaching this file. Author: Copilot agent session.
- **2026-04-24** — Systematic rewrite of `docs/SETUP.md` and `docs/USER_GUIDE.md`. Verification against source revealed that AGENT_CONTEXT itself was wrong about the GUI framework: it claimed WPF / Windows-only, but the project is Avalonia 11.3.12 targeting bare `net10.0` (cross-platform: Windows, macOS, Linux). Fixed that error in the project-at-a-glance section and the open-questions list. Other corrections caught while verifying the docs: (a) default storage tier for new watched folders is **Hot**, not Cool as both old docs claimed; (b) the app supports **four** storage tiers (Hot/Cool/Cold/Archive), not three as both old docs claimed; (c) the app supports a **portable mode** triggered by a `portable.marker` file next to the EXE, completely undocumented before; (d) the **Data Integrity Check** and **Tier Migration** views existed but were not in the USER_GUIDE TOC; (e) the **Export Bundle** button in Logs and the **Auto-export bundle on failure** option in Data Integrity were undocumented; (f) the per-extension chunk-size config table was undocumented (old SETUP just said "64KB-1MB"); (g) the LiteDB-to-SQLCipher migration path was undocumented; (h) the diagnostic-logging story (runtime UI toggle gated on the `DIAGNOSTICLOG` build constant) was incorrectly described in USER_GUIDE as a generic runtime toggle. Both docs are now flagged in the Documentation trust policy as "verified-current" with an explicit per-commit maintenance protocol listing the categories of change that REQUIRE updating them in the same commit. Author: Copilot agent session.
- **2026-04-24** — Catch-up update missed by commit `b9d5744` (B26). The B26 commit unified the UI's small-file grouping threshold with production's 16 MB constant in `RestoreService.Batch.cs` but did not touch this file at the time, even though the file's own rules require it. This entry corrects three resulting drifts: (1) the B26 number had been pre-allocated to one of the W1 follow-up tasks in the active workstreams; renamed those tasks to B27a/B27b/B27c so the numbering stays unambiguous, and added an explanation of why the pre-allocated number was consumed by an unrelated fix. (2) Added B26 to the "Completed workstreams" section. (3) Added architectural fact #9 documenting `RestoreService.SmallFileThresholdBytes` as the single cross-boundary source of truth for the "small file" definition, with an explicit "do not introduce a new hard-coded copy" warning so future agents do not re-create the drift the original fix removed. Also refreshed the recent-commit-history table to include all four post-B25-bench-2 commits (`68410b5`, `a36db75`, `fe620d6`, `b9d5744`), updated the conventions-section line that said "current series is at B25" to point at B26, and bumped the cited total commit count from 153 to 157. Lesson recorded: the per-commit AGENT_CONTEXT-maintenance rule applies to every code commit that introduces a new architectural fact or a new architectural seam, not just to commits that change something already documented in the file. Author: Copilot agent session.
- **2026-04-24** — Hardened the maintenance rule itself in response to user audit. Root cause of the B26 miss: the previous "UPDATE this file when ANY of the following happens" wording described triggers as outcomes evaluated AFTER the commit, with no pre-commit checkpoint and no explicit "introduces a new architectural fact" trigger to balance the existing "discovers" trigger. Three changes: (1) Replaced the soft trigger list with a numbered nine-item **Pre-commit checklist** at the top of the file, framed as "before running git commit, walk through these questions" so the audit happens BEFORE the commit lands rather than after. Each item is a concrete yes/no question; question 1 explicitly names the categories the agent has previously rationalized away (new public API on a Core type, new cross-project constant, new env var, new build constant, new partial-class seam, etc). Added a "Hidden failure mode" subsection naming the specific bad reasoning ("this commit only changes code, not docs") that caused the B26 miss, citing commit `df0df53` as the catch-up. (2) Mirrored the rule into `.github/copilot-instructions.md` so a fresh session sees it before even reaching this file: top-of-file UPDATE summary now includes "introduced" alongside "discovered" and explicitly mentions B-numbered commits / new public APIs as triggers; Git commit policy section now starts with an "audit AGENT_CONTEXT before committing" bullet that references the pre-commit checklist by name and warns about the exact bad reasoning that caused the B26 miss. (3) Renamed the file's old "Edit policy" section to "Edit policy (style)" to make clear it covers formatting only, while the load-bearing rule lives in the Pre-commit checklist. No change to the substance of any other section. Author: Copilot agent session.
- **2026-04-24** — B27 benchmark rewrite landed in the working tree (uncommitted) ahead of the real-hardware run. User flagged that the original B25-bench-2 benchmarks were compromised by a 28 GB disk cap on a different PC, and that any future numerical comparison must be same-hardware. Three new big-scale workloads (`media-library-500`, `production-scale-3000`, `huge-outlier-mixed`) added to `BackupBenchmarkBase` along with a `MemoryLimitMBOverride` virtual hook (so 16 GB MemoryBudget can be applied via `LocalDatabaseService.SaveConfiguration` without touching production code) and a disk-space preflight in `BaseGlobalSetup`. Five new benchmark classes added: `ProductionScaleBackupBenchmark`, `LargestFirstSchedulingBigScaleBenchmark`, `TwoTierFileSplitBigScaleBenchmark`, `AdaptiveChunkConcurrencyBigScaleBenchmark`, `MemoryBudgetBenchmark`. Pre-B27 small-workload benchmarks and their xmldoc result tables are deliberately untouched per user directive (keep historical numbers as-is for continuity), but they are now flagged as i7-9700K data and must not be quantitatively compared to numbers from any other PC. Three updates to this file: (1) added architectural fact #10 documenting the `BackupBenchmarkBase` seam (`MemoryLimitMBOverride`, `BigWorkloads`, disk preflight); (2) added a new "Same-hardware benchmark discipline" subsection under Hard Rules capturing the never-cross-PC-compare rule and noting the four pre-B27 benchmark classes whose embedded result tables are i7-9700K data; (3) renamed W1 from "act on B25-bench-2 findings" to "re-baseline at production scale on this hardware first, then act", added a new B27d follow-up gated on `MemoryBudgetBenchmark` results, and folded W2 (LPT regression investigation) into B27 since `huge-outlier-mixed` is the decisive test. No commit yet — the commit will land after the real-hardware run produces numbers, with the result tables filled into the new benchmarks' xmldocs in the same B27 commit. During this update I twice broke the numbering of the architectural-facts list while editing fact #10 in (had to restore fact #9's body verbatim from this file's earlier maintenance-log quote and re-append fact #10 at the end). Lesson: when adding to a numbered list near the end, append at the end with a verified surrounding-context match, do not try to insert into the middle. Author: Copilot agent session.
- **2026-04-27** — B28 commit. User reported that on startup, after entering the correct password and landing on the Sync tab, the **left** (Local Files) pane was empty until the user clicked Refresh; the **right** (Azure) pane populated correctly. Root cause: `MainWindowViewModel.Commands.Navigation.RefreshLocalFilesAsync` rebuilt the `LocalFileTreeRoots` collection via `BulkObservableCollection.ReplaceAll`, which raises a single `NotifyCollectionChangedAction.Reset` event. Avalonia's `TreeView` with a `HierarchicalDataTemplate` drops Reset events that arrive before the control's first layout pass, and the startup-unlock sequence is exactly that pattern: `TryUnlockWithPasswordAsync` calls `RefreshLocalFilesAsync` while `SyncView` is still `IsVisible="False"` (the visible pane only flips when `MainWindow.HandleStartupFlowAsync` sets `CurrentView = "Sync"` AFTER unlock returns). The right-pane `FileTreeRoots` works because `BuildFileTree` uses `Clear()` + per-item `Add()` -- per-item Add events ARE picked up correctly when the `TreeView` later measures. Fix: mirror the working right-pane pattern for the left tree (Clear + foreach Add on `LocalFileTreeRoots`); keep `LocalFilesFlatList.ReplaceAll` because the `ListBox`-bound flat list is unaffected by the Reset-while-collapsed footgun and the per-item-Add cost matters at the flat list's tens-of-thousands-of-files scale. Also added the missing `OnPropertyChanged(nameof(FilteredLocalFiles))` mirror of the line-378 right-pane invalidation so the flat-list view re-evaluates after the underlying collection rebuild. Documented the footgun in a new "DO NOT use ReplaceAll for TreeView with HierarchicalDataTemplate" paragraph on `BulkObservableCollection`'s class summary so the next agent considering a `ReplaceAll` for a tree-bound collection is warned in the source. Also corrected an FAQ entry near the bottom of this file that said "`MemoryLimitEnabled` is **disabled by default**" -- that was already wrong as of B27 (which flipped the default to true / 8 GB) and slipped through that commit's pre-commit audit; this entry corrects it. 766 of 766 tests pass. Lesson: when reviewing a B-numbered commit's pre-commit checklist, audit FAQ-style explanatory prose in this file too, not just architectural facts and workstream-tracking sections; a default-value flip is the kind of "non-obvious behavior introduced" change that question Q1 explicitly names. Author: Copilot agent session.

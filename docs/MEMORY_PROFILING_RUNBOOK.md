# Memory Profiling Runbook

A step-by-step guide for capturing detailed memory data from real AzureBackup
backup and restore workloads, so the results can be reviewed to find and
explain memory-budget overshoots.

This runbook produces three complementary artifacts per scenario:

1. The application's own `[mem]` telemetry stream (from the daily log).
2. Live runtime counters (`dotnet-counters`) captured to CSV.
3. One or more heap snapshots (`dotnet-gcdump`) taken at peak working set.

Together these answer: *how much* memory was used, *which heap* held it, and
*which objects* dominated the retained graph.

---

## 0. Prerequisites (one-time)

### 0.1 Build a Debug build

**Run a Debug build for the entire profiling session.** This is required:

- In Release the in-app memory telemetry (`[mem]` lines) is gated off
  (`BackupOrchestrator.EnableMemoryTelemetry` defaults to the `DIAGNOSTICLOG`
  compile state). In Debug it is on and the lines are written to the daily log.
- The daily `.log` file, the `metrics/` JSONL, and per-file `.diag` files only
  exist under `DIAGNOSTICLOG` (Debug).

```pwsh
cd C:\Users\midesjar.REDMOND\source\repos\azurebackup
dotnet build -c Debug
```

> The heap-detail telemetry (`loh`, `poh`, `heapCommit`, `frag`, `nonHeap`, ...)
> only appears on the `[mem]` lines, so a Debug build is what makes the whole
> "which heap is growing" analysis possible without attaching a profiler.

### 0.2 Install the .NET diagnostic tools (global)

```pwsh
dotnet tool install -g dotnet-counters
dotnet tool install -g dotnet-gcdump
dotnet tool install -g dotnet-trace
```

If already installed, update them:

```pwsh
dotnet tool update -g dotnet-counters; dotnet tool update -g dotnet-gcdump; dotnet tool update -g dotnet-trace
```

### 0.3 Know where the data lands

| Mode | Data directory (logs / metrics / db) |
| --- | --- |
| Installed (default) | `%LocalAppData%\AzureBackup\` |
| Portable (a `portable.marker` file sits next to the .exe) | the executable's own folder |

For the rest of this runbook the data directory is referred to as `$DATA`.
Set it for your session (installed mode shown):

```pwsh
$DATA = Join-Path $env:LOCALAPPDATA 'AzureBackup'
```

Key files inside `$DATA`:

- `azurebackup-YYYY-MM-DD.log` — contains the `[mem]` telemetry lines.
- `metrics\throughput-YYYY-MM-DD.jsonl` — structured per-file / per-operation
  metrics and decision records.
- `diagnostics\*.diag` — per-file diagnostics written on errors.

### 0.4 Create an output folder for this session

```pwsh
$RUN = "C:\memprofile\$(Get-Date -Format yyyyMMdd-HHmmss)"; New-Item -ItemType Directory -Force -Path $RUN | Out-Null; $RUN
```

### 0.5 (Optional) tighten the telemetry cadence

By default the `[mem]` reporter samples every 30 seconds. For a short workload
that is too coarse. The cadence is controlled by
`BackupOrchestrator.MemoryReporterIntervalOverride`. If you want denser samples
for a manual run, set it from your launch path (or temporarily lower the
default while profiling). A 2–5 second cadence is a good balance for a
multi-minute workload.

---

## 1. The standard capture loop (used by every scenario)

Each scenario below follows the same three-window pattern. Open **three**
PowerShell windows.

**Window A — launch the app under test.** Start AzureBackup (Debug build) and
note its process name is `AzureBackup`.

```pwsh
cd C:\Users\midesjar.REDMOND\source\repos\azurebackup
dotnet run -c Debug --project src\AzureBackup
```

**Window B — live counters to CSV.** Start this the moment the workload begins.

```pwsh
dotnet-counters collect -n AzureBackup --refresh-interval 1 --format csv -o "$RUN\counters-SCENARIO.csv" --counters System.Runtime
```

Counters of interest in the CSV: `working-set`, `gc-heap-size`,
`gen-0/1/2-size`, `loh-size`, `poh-size`, `gc-fragmentation`,
`alloc-rate`, `time-in-gc`, `gc-committed`.

**Window C — heap snapshot at peak.** Watch Window B (or Task Manager) and the
moment working set is at its **peak** for the workload, grab a gcdump:

```pwsh
dotnet-gcdump collect -n AzureBackup -o "$RUN\peak-SCENARIO.gcdump"
```

Take a second one **after** the operation settles (so the diff shows what was
released vs retained):

```pwsh
dotnet-gcdump collect -n AzureBackup -o "$RUN\settled-SCENARIO.gcdump"
```

**After the workload completes**, copy the telemetry for that scenario:

```pwsh
Copy-Item "$DATA\azurebackup-*.log" "$RUN\log-SCENARIO.log"; Copy-Item "$DATA\metrics\throughput-*.jsonl" "$RUN\metrics-SCENARIO.jsonl" -ErrorAction SilentlyContinue
```

> For each scenario, replace `SCENARIO` with the scenario id (e.g. `s1-largefile`).

---

## 2. Workload scenarios

Run these against a **real** Azure Blob Storage account (the whole point is to
capture the real Azure SDK / TLS / connection-pool residency the in-memory
benchmark cannot reproduce). Use a throwaway container.

For each scenario, follow the capture loop in section 1.

### Scenario S1 — Large-file backup (the budget-binding case)
- **Data:** 3–10 files of 1–8 GB each (e.g. video / VM images). This produces
  many simultaneous large chunk buffers and is where the budget is most likely
  to bind.
- **Action:** in the app, select the large-file folder and run **Backup**.
- **Capture:** counters throughout; gcdump at peak working set; settled gcdump
  at completion.
- **Why:** the LOH / chunk-buffer-pool residency shows up here. Watch `loh=`
  and `frag=` in the log and `loh-size` in the counters.

### Scenario S2 — Many-small-file backup (metadata + small-pool churn)
- **Data:** 50,000–200,000 small files (1 KB – 1 MB).
- **Action:** select that tree and run **Backup**.
- **Why:** stresses the small-chunk pool, the SQLite catalog writes, and per-file
  metadata. Watch `smPool` and the gen-0/1 collection counts.

### Scenario S3 — Mirror sync to Azure
- **Data:** a folder already partially backed up, with some files changed.
- **Action:** run **Mirror to Azure**.
- **Why:** the mirror path has its own orchestration; confirms the budget
  behaves the same as a plain backup.

### Scenario S4 — Single large-file restore
- **Data:** restore one of the S1 large files to a fresh local folder.
- **Action:** **Restore** that file.
- **Why:** the restore pipeline rents plaintext + encrypted buffers and streams
  chunks; this is the restore-side budget path. Watch `nonHeap=` — restore pulls
  bytes through the Azure SDK download path.

### Scenario S5 — Batch restore (many files)
- **Data:** restore the whole S2 small-file set.
- **Action:** **Restore** the batch.
- **Why:** the two-tier parallel restore core under load.

### Scenario S6 — Mirror sync to local
- **Data:** mirror a backed-up tree down to an empty local folder.
- **Action:** **Mirror to Local**.
- **Why:** the batch restore plus the file-classification phase.

### Scenario S7 — Idle baseline
- **Action:** launch the app, unlock, and let it sit idle for 2 minutes with no
  operation running. Capture counters + one gcdump.
- **Why:** the control. Establishes the steady-state working set so every other
  scenario's growth can be measured against it.

---

## 3. What to record per scenario

For each scenario, note from the **last `[mem]` line at peak** (grep the log):

```pwsh
Select-String -Path "$RUN\log-SCENARIO.log" -Pattern '\[mem\]' | Select-Object -Last 5
```

Capture these fields into a table (one row per scenario):

| Field (from `[mem]` line) | Meaning |
| --- | --- |
| `workingSet` | Total OS-billed residency at peak |
| `budget used / total / peak` | What the orchestrator's accounting saw |
| `unaccounted` | Residency the budget could not see |
| `loh` / `poh` | Managed Large / Pinned Object Heap size |
| `heapCommit` / `heapSize` / `frag` | GC committed vs live vs fragmented |
| `nonHeap` | `workingSet - heapCommit` — the native/off-heap signal |
| `stalls` / `oversized` | Budget throttle activity |
| `gcPause%` / `gcCollections` | GC pressure |

**The single most important read:** compare `nonHeap` to `loh + poh + frag`.

- If **`loh` / `poh` / `frag` dominate** the overshoot → it's a managed-heap
  problem and the **gcdump retained graph will name the owning objects**.
- If **`nonHeap` dominates** with low `heapCommit` → the overshoot is native
  (Azure SDK staging buffers, TLS, OS file-cache pages). A managed gcdump will
  **not** show it; the next tool is an ETW / native-allocation trace
  (section 4.3), not a gcdump.

---

## 4. Optional deeper captures

### 4.1 Open a gcdump

Open each `.gcdump` in Visual Studio (File ▸ Open) or PerfView. Sort by
**retained size** and look at the top dominators. For a backup overshoot the
usual suspects are `byte[]` (chunk buffers), Azure SDK `MemoryStream` /
response buffers, and `ChunkBufferPool` retained arrays.

### 4.2 Diff two gcdumps

In Visual Studio, open the **peak** dump and use "Compare to…" against the
**settled** dump. The growth column shows what accumulated and never came back.

### 4.3 Allocation-call-stack trace (when nonHeap is NOT the story)

If the heap is the suspect and you want allocation call stacks:

```pwsh
dotnet-trace collect -n AzureBackup --providers Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1:5 -o "$RUN\gc-trace.nettrace"
```

Open the `.nettrace` in Visual Studio or PerfView ("GC Heap Alloc Stacks").

### 4.4 Force a reproducible OOM (optional, advanced)

To make an overshoot deterministic, launch with a hard heap cap and capture a
dump at the limit:

```pwsh
$env:DOTNET_GCHeapHardLimit = "0x180000000"  # 6 GB, hex bytes
dotnet run -c Debug --project src\AzureBackup
```

(Unset afterwards: `Remove-Item Env:\DOTNET_GCHeapHardLimit`.)

---

## 5. Package the results for review

```pwsh
Compress-Archive -Path "$RUN\*" -DestinationPath "$RUN.zip"; "Bundle: $RUN.zip"
```

Hand over `$RUN.zip`, which contains per-scenario: `counters-*.csv`,
`peak-*.gcdump`, `settled-*.gcdump`, `log-*.log`, `metrics-*.jsonl`. Include the
scenario table from section 3.

When presenting findings, the most useful summary is the section-3 table across
all seven scenarios plus the top-5 retained-size dominators from the S1 and S4
peak gcdumps — those two scenarios are where the budget is most likely to bind
on the backup and restore sides respectively.

---

## 6. Quick reference — all commands in order

```pwsh
# one-time
dotnet build -c Debug
dotnet tool install -g dotnet-counters; dotnet tool install -g dotnet-gcdump; dotnet tool install -g dotnet-trace
$DATA = Join-Path $env:LOCALAPPDATA 'AzureBackup'
$RUN  = "C:\memprofile\$(Get-Date -Format yyyyMMdd-HHmmss)"; New-Item -ItemType Directory -Force -Path $RUN | Out-Null

# per scenario (three windows)
# A:
dotnet run -c Debug --project src\AzureBackup
# B (start with the workload):
dotnet-counters collect -n AzureBackup --refresh-interval 1 --format csv -o "$RUN\counters-s1.csv" --counters System.Runtime
# C (at peak, then at settle):
dotnet-gcdump collect -n AzureBackup -o "$RUN\peak-s1.gcdump"
dotnet-gcdump collect -n AzureBackup -o "$RUN\settled-s1.gcdump"
# after:
Copy-Item "$DATA\azurebackup-*.log" "$RUN\log-s1.log"; Copy-Item "$DATA\metrics\throughput-*.jsonl" "$RUN\metrics-s1.jsonl" -ErrorAction SilentlyContinue

# package
Compress-Archive -Path "$RUN\*" -DestinationPath "$RUN.zip"
```

# Azure Backup Tool — User Guide

This guide covers the everyday tasks: configuring the app, backing up files, restoring them, and getting useful diagnostic information when something goes wrong. For installation and the underlying technical design, see `docs/SETUP.md`.

Last verified against code at commit time of this file. If you find a discrepancy between this document and the running app or current source, update this document in the same commit as the code change that revealed the discrepancy. See `.github/copilot-instructions.md` "Documentation trust policy".

---

## Table of contents

1. [First-time setup](#first-time-setup)
2. [Returning users (daily unlock)](#returning-users-daily-unlock)
3. [The Sync view](#the-sync-view)
4. [Backing up files](#backing-up-files)
5. [Restoring files](#restoring-files)
6. [Mirror sync](#mirror-sync)
7. [Deleting files from Azure](#deleting-files-from-azure)
8. [Managing watched folders](#managing-watched-folders)
9. [Storage Health view](#storage-health-view)
10. [Tier Migration view](#tier-migration-view)
11. [Data Integrity Check view](#data-integrity-check-view)
12. [Settings](#settings)
13. [Logs and diagnostic bundles](#logs-and-diagnostic-bundles)
14. [Drag and drop](#drag-and-drop)
15. [Operation previews](#operation-previews)
16. [Troubleshooting](#troubleshooting)
17. [Best practices](#best-practices)

---

## First-time setup

When you first launch the app the **Settings** view opens with status "Not Configured".

### Step 1 — Choose an authentication method

In the **Authentication Method** card, pick one:

- **Connection String (Personal Accounts)** — paste a connection string from the Azure Portal.
- **Microsoft Entra ID (Work/School)** — click **Sign in with Microsoft** to do a browser-based interactive sign-in (you have 2 minutes to complete it). Then enter your **Storage Account Name** (just the name, not the full URL).

### Step 2 — Configure Azure storage

If using a connection string:

1. Paste the connection string (Azure Portal → Storage Account → Access keys).
2. Enter a **Container Name** (default `backup`).
3. Click **Test Connection** to verify.

If using Entra ID:

1. Enter the **Storage Account Name**.
2. Enter a **Container Name**.
3. Click **Test Connection** to verify. Your account must hold the **Storage Blob Data Contributor** role on the storage account.

### Step 3 — Set your encryption password

1. Enter a strong password.
2. Confirm it by typing it again. A red "Passwords do not match!" warning appears if they differ.

The password derives the AES-256 key that encrypts every byte before upload. **There is no recovery path.** If you forget it, the backed-up data is unrecoverable by design.

### Step 4 — Initialize and connect

Click **Initialize & Connect**. The app will:

- Create the local database (`backup.db`), encrypted with SQLCipher using your password.
- Encrypt and store your connection string inside `backup.db`.
- Connect to Azure Storage.

You should see a green **Unlocked and Connected** indicator. The app then auto-loads any pre-existing watched folders and Azure files.

### Step 5 — Add folders to watch

Watched folders are added from the **Sync** view, not from Settings:

1. Open the **Sync** view.
2. Click the **+** button in the **Local Files** panel header.
3. Browse to the folder.

Once added, you can configure each folder's storage tier and exclusion patterns from the Settings **Watched Folders** card.

---

## Returning users (daily unlock)

Whenever the app launches and finds an existing `backup.db`, a **password dialog** appears immediately. Type your password and press **Enter** (or click **OK**). The app unlocks the database, reconnects to Azure, and loads your files.

If you cancel the dialog, the app opens in the locked state. You can unlock later from Settings by entering your password and clicking **Unlock**.

After 5 wrong-password attempts the app applies a temporary lockout to slow brute-force attacks.

---

## The Sync view

The Sync view is the main workspace. It shows two side-by-side panels with a resizable splitter.

| Area | What it is |
|---|---|
| **Toolbar** (top) | Summary counts, primary actions, status indicator |
| **View controls** (below toolbar) | Tree/list toggle, expand/collapse, search filter, selection controls |
| **Local Files** (left panel) | Files in your watched folders, with backup status per file |
| **Azure Backup** (right panel) | Files stored in Azure, with storage-tier badges |
| **Progress panel** (when active) | Overall and per-file progress, speed, ETA, cancel button |
| **Actions panel** (when files are selected) | Contextual actions for the current selection |

### Status indicators

| Status | Dot color | Meaning |
|---|---|---|
| Monitoring for Changes | Green | File watcher is running; changes back up automatically |
| Ready (Not Monitoring) | Gray | Unlocked but not actively watching for changes |
| Locked (Enter Password) | Gray | Needs the password to unlock the database |
| Not Configured | Gray | First-time setup required |

### Toolbar buttons

| Button | What it does |
|---|---|
| **Sync Selected** | Backup the checked local files AND restore the checked Azure files in one operation |
| **Start Monitoring** / **Stop Monitoring** | Start or stop the real-time file watcher |
| **Refresh** | Reload both local and Azure file lists |
| **Tree / List** | Switch between hierarchical folder tree and flat scrollable list |

### View controls

- **Expand All** / **Collapse All** — only visible in tree view.
- **Search** — type a filename or path fragment to filter both panels at once. Click **X** to clear.
- **Select All** / **Deselect All** — bulk toggle.

---

## Backing up files

### Automatic monitoring

1. Click **Start Monitoring**.
2. The watcher detects file changes inside every enabled watched folder.
3. Changed files are queued and uploaded.
4. Status dot turns green while monitoring.

The watcher waits up to **5 minutes** for files that are temporarily locked by another process before giving up; locked-out files are skipped and retried on the next sync cycle.

### Manual backup of a selection

1. Check files or folders in the **Local Files** panel.
2. Click **Backup Selected** in the actions panel at the bottom.
3. The **Preview Dialog** appears showing what will be uploaded (new, modified, unchanged).
4. Optionally uncheck files to exclude, then click **Proceed**.
5. Watch the progress panel for overall and per-file progress.

### Right-click context menu (tree view)

| Item | What it does |
|---|---|
| Backup Selected | Upload checked files to Azure |
| Force Re-upload Selected (B43) | Re-upload checked files even when nothing changed; bypasses dedup AND the metadata-skip fast path. Use after an integrity-check non-repairable failure or when remote bytes are suspected of being corrupt for a known-good local file. The Preview Dialog still runs so you can confirm the byte volume. |
| Mirror Sync to Azure | Mirror the selected single watched root to Azure (single-root selection only) |
| Add Watched Folder... | Add a new folder to the watch list |
| Remove from Watch List | Remove the selected folder (does not delete from Azure) |
| Select All / Deselect All | Bulk selection |
| Expand All / Collapse All | Tree navigation |
| Refresh | Reload local file list |

---

## Restoring files

### Restore selected files

1. In the **Azure Backup** panel, check the files to restore.
2. Either tick **Restore to original location** to put them back where they were, or click **Browse...** for a different destination folder.
3. Click **Restore Selected**.
4. The Preview Dialog shows what will be created or overwritten. Review and click **Proceed**.

### Path remapping

When you select a folder node in the Azure tree, a **Remap path** panel appears. This redirects an entire subtree to a different location:

1. Select a folder node.
2. Click **Browse...** in the remap panel, or type a target path.
3. Click **Set** to apply.
4. Restored files under that subtree go to the remapped path.
5. Click **Clear** to reset.

### What happens during restore

1. Encrypted chunks are downloaded from Azure in parallel.
2. Each chunk is decrypted (AES-256-GCM) using the key derived from your password. Tampered or corrupt chunks fail the GCM tag check immediately.
3. The original file is reassembled from its chunks.
4. A SHA-256 hash check verifies end-to-end integrity against the stored metadata.
5. The file is written to the destination.

You need the same password that was used during backup. There is no recovery path.

---

## Mirror sync

Mirror sync makes a local folder match the Azure backup exactly: missing files are restored, outdated files are updated, and **extra local files are deleted**.

1. Select a folder node in the Azure tree.
2. Use the **Remap path** panel to set the target local folder.
3. Click **Mirror Sync** (or right-click → Mirror Sync).
4. The Preview Dialog shows every planned action (create, overwrite, delete, skip).
5. Review carefully — deletions are permanent. Uncheck any items you want to spare.
6. Click **Proceed**.

---

## Deleting files from Azure

1. Check the files or folders to remove in the **Azure Backup** panel.
2. Click **Delete from Azure** in the actions panel (or right-click → Delete from Azure).
3. Review the Preview Dialog and click **Proceed**.

Deleted files cannot be recovered from Azure after this operation. Local copies are not touched.

---

## Managing watched folders

### Add a folder

1. Open the **Sync** view.
2. Click the **+** button in the Local Files panel header.
3. Browse to the folder.

### Remove a folder

1. Select the folder in the Local Files tree.
2. Click the **-** button in the panel header (or right-click → Remove from Watch List).

Removing a folder does not delete its backed-up files from Azure; they remain available for restore.

### Configure a folder

1. Open **Settings**.
2. Click a folder in the **Watched Folders** card.
3. Adjust:
   - **Storage Tier** — Hot (default for new folders), Cool, Cold, or Archive.
   - **Exclusion Patterns** — semicolon-separated glob patterns to skip.

### Temporarily disable a folder

Uncheck the checkbox next to a folder in the Settings watched-folders list. The folder is no longer monitored until re-enabled.

### Common exclusion patterns

| Pattern | What it excludes |
|---|---|
| `*.tmp` | Temporary files |
| `*.log` | Log files |
| `*.bak` | Backup files |
| `.git` | Git repository data |
| `node_modules` | npm dependencies |
| `bin;obj` | .NET build output |
| `.vs` | Visual Studio cache |
| `thumbs.db` | Windows thumbnail cache |

Example combined value: `*.tmp;*.log;*.bak;node_modules;.git;bin;obj;.vs;thumbs.db`

---

## Storage Health view

The **Storage Health** view gives visibility into your chunk-level storage and tools for maintenance.

### Chunk index summary

| Metric | What it means |
|---|---|
| Total Chunks | Number of content-addressed chunks stored in Azure |
| Orphaned | Chunks no longer referenced by any file (wasted space) |
| Deduplicated | Chunks shared by multiple files (storage saved) |

The card also shows the timestamp of the last index rebuild and the last Azure sync.

### Storage tier breakdown

Visual cards per tier showing chunk count and total bytes stored:

- **Hot** — highest access speed and cost.
- **Cool** — recommended for backup data accessed infrequently.
- **Cold** — lower cost still, milliseconds latency.
- **Archive** — cheapest storage, hours of retrieval latency.

### Orphan detection and cleanup

Orphaned chunks are blobs in Azure that no file references. They waste storage.

1. Click **Scan for Orphans**.
2. Review the list (hash, size, tier, upload date, original file).
3. Use **Select All** or check individual rows.
4. Click **Delete Selected** to remove them from Azure.

### Index management

| Action | What it does |
|---|---|
| Backup to Azure | Upload the chunk index to Azure for disaster recovery |
| Restore from Azure | Download the index from Azure (e.g. after reinstalling) |
| Rebuild from Azure | Rebuild the index by scanning all metadata blobs in Azure. Use this if the index is corrupted or out of sync. |

### Catalog database file

The **Catalog Database File** card runs a low-level diagnostic against the encrypted SQLite/SQLCipher catalog file on disk. This is **not** the same as the Data Integrity check — that one verifies your backed-up files in Azure, while this one verifies the local catalog file itself.

| Action | What it does |
|---|---|
| Verify Database File | Runs `PRAGMA cipher_integrity_check` (SQLCipher per-page HMAC) and `PRAGMA integrity_check` (SQLite b-tree structure) against the open catalog and shows the report below the button. |

When to use it:

- An action against the catalog (e.g. starting a Data Integrity check, saving a backed-up file row) fails with `SQLite Error 11: 'database disk image is malformed'` or a similar SQLite/SQLCipher error.
- The Data Integrity tab shows `Check could not start: The local backup catalog database file is corrupted on disk…` — that message is the typed signal that the catalog itself, not your backed-up files, is the problem.

How to read the report:

- A healthy catalog shows `ok (no failing pages)` for the cipher pragma and a single `ok` row for the SQLite pragma. SQLCipher emits zero rows on success, so the empty-list shape is what "healthy" looks like.
- Any other lines describe the affected page or b-tree node. Copy the report into a support request and treat the catalog as untrustworthy until repaired (restore it from a recent backup, or rebuild the chunk index from Azure metadata via **Rebuild from Azure** above).

---

## Tier Migration view

The **Tier Migration** view lets you move existing chunks between storage tiers (Hot ↔ Cool ↔ Cold ↔ Archive) without re-uploading them. Each tier card shows current count and bytes; choose a source and target tier and the operation rewrites only the tier metadata on each chunk.

Use this when, for example, you decide that a cold archive of older media should live on the Archive tier to save monthly cost, or when promoting a frequently-restored folder back to Hot.

---

## Data Integrity Check view

The **Data Integrity Check** view re-downloads stored chunks and verifies their hashes against the recorded metadata. Use it to confirm that backed-up data is still intact (no silent storage corruption, no partial uploads, no tier-migration mishaps).

Key controls:

- **Scope selector** — choose which subset of files to verify.
- **Run** button — start the check; the progress text shows current file and chunk.
- **History expander** — last 10 runs, click a row to see its failures.
- **Auto-export bundle on failure** checkbox — when enabled, the first failure in a run automatically writes a diagnostic ZIP to the data directory. The bundle excludes the encrypted database and salt files (sensitive material is redacted), so it is safe to share in a bug report.
- **Auto-repair on failure** checkbox (B43) — defaults to **on**. When enabled, the engine silently re-uploads and re-checks any file that fails for a repairable reason (missing-blob, wrong-size, md5-mismatch, crc-mismatch, decrypt-failed, byte-differ). Disable it for a forensic run that needs to see the un-repaired failure shape (the failure row and `.diag` file are then preserved exactly as the first pass produced them).
- Per-file `.diag` files for failures can be opened in your default editor or revealed in the file manager via right-click.

### Automatic repair on failure (B42)

When the integrity check finds a repairable failure (missing blob, wrong size, MD5 mismatch, CRC mismatch, decryption failure, or byte-for-byte mismatch against your local file), it automatically forces a re-upload of the affected file and re-runs the check on it. If the second pass succeeds, **no `.diag` file is written** and **no failure row is recorded** — the run reports the file as passing. The run summary line shows how many files were transparently repaired (e.g. `Integrity check OK -- 1043 files passed (3 auto-repaired).`), and the History expander annotates affected runs with `[auto-repaired N]` so you can see at a glance that the run was not a no-op.

The repair retry is one-shot. If the re-upload fails or the second integrity pass still fails, the failures are reported normally with a `.diag` file flushed (the summary suffix is `(post-auto-repair)` so you can tell it was the post-repair shape, not the original). Failures that re-uploading cannot fix (e.g. local file missing, engine error) are not retried.

This behaviour can be useful to confirm explicitly: delete a chunk via Storage Explorer, run a check, observe a clean result with `auto-repaired 1`. The auto-repair count is persisted on the run row (B43), so the History expander still shows `[auto-repaired N]` after restarting the app. Pre-B43 historical runs always read 0 because the count was not recorded at the time.

---

## Settings

The Settings view contains all configuration. The page is divided into **cards**:

### Authentication Method

Switch between **Connection String** and **Microsoft Entra ID** at any time. The other card hides automatically.

### Azure Storage

- **Connection String / Storage Account Name** — depending on the auth method.
- **Container Name** — the blob container (default `backup`).
- **Test Connection** — verify connectivity without saving.
- **Update Connection String** — clears the stored encrypted string so a new one can be entered. Your data and settings are preserved.
- **Save & Connect** — encrypt and save the new connection string, then reconnect.

### Password / Unlock

- Returning users: enter your password and click **Unlock**.
- New users: enter and confirm a password, then click **Initialize & Connect**.

### Watched Folders

Lists every watched folder with its enable/disable checkbox, storage-tier badge, and exclusion-pattern preview. Click a folder to configure its tier and exclusion patterns in the panel below.

Folders are added and removed from the **Sync** view (see "Managing watched folders" above), not from this card.

### Memory limit

A toggle plus a slider that puts a soft cap on the total bytes the backup pipeline holds in flight at once (chunk buffers in transit between read, encrypt, and upload). The toggle defaults to **on**. The slider's default value scales with installed RAM so the cap is always inside the safe band on every hardware tier:

| Installed RAM | Default memory limit |
|---:|---:|
| 2 GB | 512 MB |
| 4 GB | 1 GB |
| 8 GB | 2 GB |
| 16 GB | 4 GB |
| 32 GB or more | 8 GB |

The rule is "25 percent of physical RAM, snapped down to a slider step, capped at 8 GB" so a workstation with 64 GB or 128 GB still defaults to 8 GB; 25 percent of that much RAM would be more memory than the backup pipeline can usefully consume given the existing per-file and per-chunk concurrency limits. Turn the toggle off only if you have measured a specific throughput problem on your machine and want to fall back to the unlimited-budget behaviour; you can also push the slider higher than the auto-default up to total physical RAM if you have measured a benefit. The default was chosen because the `MemoryBudgetBenchmark` measurement on production-shaped workloads showed zero throughput cost even at 4 GB, while leaving the cap unlimited would let the pipeline grow to many GB on a single backup.

The slider snaps to a discrete set of values; the live label shows the chosen MB and a status color (green / amber / red) indicating how aggressive the cap is.

### Memory log lines during a backup (B36)

While a backup is running you will see periodic log lines of the form

```
[mem] backup t+30s | budget used=2048 MB / 8192 MB (25.0%) | stalls +12 (total 47) | oversized +0 (total 0) | gcHeap=2310 MB | gcLoad=12345 MB | workingSet=2680 MB | privateMem=2750 MB | unaccounted=632 MB | gcCollections=[34,8,1] | lohPool=1024 MB cached, hit=87%
```

emitted every 30 seconds from the moment a backup or mirror operation starts until it finishes. The fields are:

- `budget` — bytes acquired from `MemoryLimitMB` versus the configured cap. A percentage at or near 100 percent across many samples means the budget is binding and the pipeline is throttling.
- `stalls +N (total M)` — number of times since the previous sample that a chunk had to wait for the budget to free up before it could be admitted, plus the running total since the operation started. A non-zero rate confirms the budget is actually doing its job.
- `oversized +N (total M)` — number of chunks that bypassed the cap because they individually exceeded the entire budget (the deadlock-avoidance branch). A non-zero value here means the configured cap is smaller than the largest chunk size produced; consider raising `MemoryLimitMB` or reducing the per-extension chunk-size config.
- `gcHeap` — total managed heap as seen by the GC (`GC.GetTotalMemory`).
- `gcLoad` — system-wide memory load the GC is observing (`GCMemoryInfo.MemoryLoadBytes`).
- `workingSet` and `privateMem` — what the OS bills the process for.
- `unaccounted` — `workingSet - budget.UsedBytes`. This is the gap between what the budget thinks is in flight and what the OS sees the process holding. A small bounded value is normal (managed object headers, the SQLite connection's page cache, etc.). A growing `unaccounted` value across samples is the signature of an undercounted allocation site and should be reported.
- `lohPool=X MB cached, hit=Y%` (B37) — current residency in the operation-scoped LOH recycler pool that holds large-chunk buffers, plus the percentage of large-chunk rents that were served from the pool's cache instead of allocating fresh. A high hit rate (typically above 70 percent in steady state) confirms the recycler is doing its job; a low hit rate with high `gcHeap` growth means buffers are flowing through the pool but not being kept across chunks (e.g. a workload where every chunk lands in a different bucket size). The cached residency is bounded by the per-bucket cap so a steady value here is expected.

The first line is tagged `[mem-start]` and captures the pre-fan-out state; the last line on a given operation arrives at operation completion (just before the operation summary). Operations shorter than 30 seconds will produce only the start and end lines.

### UI scale

A slider that scales the whole UI, with a reset button next to it.

### Danger Zone — Reset Application

Securely deletes all local settings, credentials, and file-tracking data. **Files in Azure Storage are not affected.** Requires explicit confirmation. Use this if you want to start over from scratch on the same machine.

---

## Logs and diagnostic bundles

The **Logs** view shows a chronological activity log in a monospaced font.

- **Diagnostic Logging** toggle (ON / OFF) — controls runtime opt-in for service-level logging from encryption, chunking, restore, and blob services. The toggle only has an effect in builds compiled with the `DIAGNOSTICLOG` constant defined (Debug builds, by default — Release builds omit the diagnostic-log code paths entirely so the toggle does nothing in those).
- **Clear Logs** — wipes log entries from the current session.
- **Export Bundle** — zips up all logs, all `.diag` files, and throughput metrics into a single archive next to the data directory. The encrypted database and salt files are excluded so the bundle is safe to share in a bug report.

For repeatable bug reports the recommended sequence is:

1. Open Logs, turn **Diagnostic Logging** ON.
2. Reproduce the issue.
3. Click **Export Bundle**.
4. Attach the resulting ZIP to the bug report.

---

## Drag and drop

The Sync view supports drag and drop between panels:

- Drag files from the **Azure** panel to the **Local** panel to restore them.
- Drag files from the **Local** panel to the **Azure** panel to back them up.

Drop targets highlight with a colored border and label while you drag over them.

---

## Operation previews

Before any destructive or large operation (backup, restore, mirror sync, delete), a **Preview Dialog** appears showing:

- **Summary statistics** — counts of files to create, overwrite, delete, or skip.
- **Transfer size** — total bytes to upload or download.
- **File list** — every affected file with its action, size, and reason.
- **Per-file checkboxes** — uncheck files to exclude them from the operation.
- **Warning banners** — appear for operations that will delete files.

Files are grouped by action type (new, overwrite, delete, skip) in collapsible sections. Click **Proceed** to execute or **Cancel** to abort.

---

## Troubleshooting

### "Please initialize first"

You are unlocked but the app is missing a configuration step. Open Settings and click **Unlock** or **Initialize & Connect**.

### "Passwords do not match"

The password and confirmation fields differ. Re-type both carefully.

### "Invalid password"

The password does not match what was used during initial setup. Passwords are case-sensitive. After 5 wrong attempts the app applies a temporary lockout. There is no password recovery — if forgotten, the data is unrecoverable.

### Files are not being backed up

1. Confirm the folder is **enabled** (checkbox checked in Settings).
2. Confirm the file is not matched by an **exclusion pattern**.
3. Confirm **Monitoring** is started (green dot in Sync toolbar).
4. Click **Refresh** in the Sync toolbar.
5. If a single file is being skipped, it may be locked by another application — see "File locked" in `docs/SETUP.md`.

### "Connection failed"

1. Verify the connection string or storage account name.
2. Check your internet connection and any firewall rules blocking outbound HTTPS to Azure.
3. Ensure the Azure storage account and container exist.
4. Use **Test Connection** in Settings to isolate auth from sync.
5. For Entra ID, ensure you have the **Storage Blob Data Contributor** role on the storage account.

### Restore fails with an integrity error

The backed-up data may be corrupted, or your local metadata is out of sync with what is actually in Azure. Open the **Data Integrity Check** view and run a verification scope including the affected files. The history expander captures previous failures for comparison. For a deeper analysis use **Export Bundle** in Logs and attach the ZIP to a bug report.

### Application closes unexpectedly

The crash log lives in the data directory (`%LOCALAPPDATA%\AzureBackup\` in installed mode, alongside the EXE in portable mode). Common causes:

- Network interruption during a long upload or download
- Insufficient disk space for the local database / temp chunk buffers
- File access permission errors

---

## Best practices

1. Run a **Refresh** in the Sync view periodically to catch any files missed by the real-time watcher.
2. Run a **restore-to-temp-folder** test periodically to verify backups are intact end-to-end.
3. Run the **Data Integrity Check** view periodically — at least monthly for important data.
4. Use **exclusion patterns** to skip temporary files, build outputs, and caches. Backing them up just costs storage and bandwidth.
5. Keep the password in a password manager. Do not store it in plain text next to the encrypted database.
6. Do not modify files during a restore to prevent conflicts.
7. Use the **Storage Health** view to spot and clean up orphaned chunks.
8. **Backup the chunk index to Azure** (Storage Health → Backup to Azure) for disaster recovery — it lets you reconstruct the local database after a reinstall.

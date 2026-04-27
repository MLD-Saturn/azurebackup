# Azure Backup Tool — Setup Guide

Last verified against code at commit time of this file. If you find a discrepancy between this document and the running app or current source, update this document in the same commit as the code change that revealed the discrepancy. See `.github/copilot-instructions.md` "Documentation trust policy".

---

## What this app is

A zero-knowledge encrypted backup tool that syncs local files to Azure Blob Storage. All encryption happens locally before any data leaves the machine, so Azure (and Microsoft) cannot read your backups. The app uses content-defined chunking (CDC) for deduplication and bandwidth-efficient delta sync.

The application is a cross-platform Avalonia desktop app targeting .NET 10. It runs on Windows, macOS, and Linux.

---

## Azure Setup

You need an Azure Storage Account and either a connection string or Microsoft Entra ID access.

### Step 1 — Create a Resource Group

1. Open the [Azure Portal](https://portal.azure.com).
2. Search for "Resource groups" and click **+ Create**.
3. Pick a subscription, give the group a name (e.g. `rg-backup`), and choose a region close to you.
4. Click **Review + create**, then **Create**.

### Step 2 — Create a Storage Account

1. Search for "Storage accounts" and click **+ Create**.
2. **Basics** tab:
   - Subscription and resource group: as above.
   - Storage account name: must be globally unique, lowercase, no special characters (e.g. `stbackup<yourname>`).
   - Region: same as the resource group.
   - Performance: **Standard**.
   - Redundancy: **LRS** for cheapest, **GRS** for cross-region disaster recovery.
3. **Advanced** tab:
   - Require secure transfer: **Enabled**.
   - Enable blob public access: **Disabled**.
   - Enable storage account key access: **Enabled** (only required if you intend to use the connection-string auth path).
   - Default access tier: **Hot** or **Cool** depending on how often you expect to restore. The app defaults new watched folders to **Hot**; you can change the per-folder tier later in Settings.
4. **Review + create**, then **Create**.

### Step 3 — Get credentials

You can authenticate the app two ways. Pick one.

**Option A — Connection string** (simplest, works with personal accounts):

1. Open the new Storage Account.
2. Under **Security + networking**, click **Access keys**.
3. Click **Show** next to one of the keys.
4. Copy the **Connection string** (starts with `DefaultEndpointsProtocol=https;AccountName=...`).
5. Treat it like a password.

**Option B — Microsoft Entra ID** (work/school accounts):

1. On the Storage Account, open **Access Control (IAM)**.
2. Add a role assignment giving your user account the **Storage Blob Data Contributor** role on this storage account.
3. You will sign in interactively from the app; no key needed.

---

## Building from source (developers)

Prerequisites: .NET 10 SDK. The repo includes a `global.json` pinning the SDK version; `dotnet --version` from the repo root will tell you which one.

From the repo root:

```
dotnet restore azurebackup.sln
dotnet build azurebackup.sln -c Debug
```

NuGet restore handles the native SQLCipher binaries automatically through the `SQLitePCLRaw.bundle_e_sqlcipher` package.

To run the desktop app from source:

```
dotnet run --project src/AzureBackup -c Debug
```

To run the test suite:

```
dotnet test tests/AzureBackup.Tests/AzureBackup.Tests.csproj -c Debug
```

For the BenchmarkDotNet performance suite, see `benchmarks/AzureBackup.Benchmarks/`. Benchmarks are Release-only and not part of CI.

---

## Publishing a portable single-file build

The `src/AzureBackup` project is pre-configured for single-file self-contained publish (see `<PublishSingleFile>`, `<SelfContained>`, `<EnableCompressionInSingleFile>`, `<PublishReadyToRun>` in `src/AzureBackup/AzureBackup.csproj`).

Pick the runtime identifier for your target OS:

```
dotnet publish src/AzureBackup -c Release -r win-x64
dotnet publish src/AzureBackup -c Release -r linux-x64
dotnet publish src/AzureBackup -c Release -r osx-x64
dotnet publish src/AzureBackup -c Release -r osx-arm64
```

The output ends up under `src/AzureBackup/bin/Release/net10.0/<rid>/publish/`.

### Portable mode

The app supports a "portable" mode that stores all data alongside the executable instead of in `LocalAppData`. To enable it, drop a file named `portable.marker` (any contents, even empty) next to the published executable. On launch the title bar shows `(Portable)` and `AppMode.DataDirectory` resolves to the executable directory.

Without the marker file the app runs in installed mode. See "File locations" below.

---

## First-time configuration of the running app

1. Launch the app.
2. The **Settings** view appears with status "Not Configured".
3. Pick an **Authentication Method**:
   - **Connection String (Personal Accounts)** — paste the connection string from Step 3A above.
   - **Microsoft Entra ID (Work/School)** — click **Sign in with Microsoft** to do a browser-based interactive sign-in (you have 2 minutes), then enter the Storage Account Name (just the name, not the full URL).
4. Enter a **Container Name** (default `backup`).
5. Click **Test Connection** to verify credentials.
6. Enter and confirm a **Password**. This password derives the AES-256 key used to encrypt every byte sent to Azure.

   **If you forget this password your data cannot be recovered.** There is no reset, no recovery question, no backdoor.
7. Click **Initialize & Connect**. The app creates `backup.db` (encrypted with SQLCipher), stores your encrypted connection string inside it, and connects to Azure.
8. Open the **Sync** view, click the **+** in the Local Files panel header to add folders to watch, then click **Start Monitoring**.

---

## How it works (technical overview)

### Encryption envelope

```
Your Password
    |
    v
Argon2id KDF  (memory=64 MB, lanes=8, iterations=3, salt is per-database, stored locally)
    |
    v
256-bit AES key
    |
    v
AES-256-GCM per chunk  (random 12-byte nonce per chunk)
    |
    v
[magic(4) | version(1) | nonce(12) | ciphertext(N) | tag(16) | crc32(4)]   = 37 bytes overhead
    |
    v
Encrypted blob in Azure
```

- **Argon2id** — memory-hard key derivation, resistant to GPU attacks.
- **AES-256-GCM** — authenticated encryption; tampered ciphertext will fail to decrypt.
- **CRC32 trailer** — fast tamper detection for partial reads, separate from the GCM tag.
- **Zero-knowledge** — Azure only sees opaque blobs whose names are SHA-256 hashes of the encrypted content. No filenames, no folder structure, no clear-text metadata.

### Content-defined chunking (deduplication)

Files are split into variable-sized chunks using a Rabin-style rolling hash (window 48, prime 31). Chunk size is configured per file extension; the defaults aim for "small chunks for small files, large chunks for media":

| File extension family | Min — Max chunk |
|---|---|
| `.txt` and similar tiny text | 16 KB — 128 KB |
| Default (no extension match) | 64 KB — 1 MB |
| Photos (`.jpg` and similar) | 256 KB — 4 MB |
| Video (`.mkv` and similar) | 1 MB — 64 MB |

Files larger than **500 MB** ignore the per-extension config and use 16 MB — 128 MB chunks regardless of type, for upload throughput. See `src/AzureBackup.Core/Services/ChunkingService.cs` for the authoritative table.

Each chunk is hashed; only chunks whose content has changed are uploaded. Chunks shared between files are stored once.

### Storage tiers

The app supports four Azure tiers, configurable per watched folder:

| Tier | Storage cost | Retrieval cost | Retrieval latency |
|---|---|---|---|
| **Hot** (default for new folders) | Highest | Lowest | Milliseconds |
| **Cool** | Lower | Higher | Milliseconds |
| **Cold** | Lower still | Higher still | Milliseconds |
| **Archive** | Lowest | Highest | Hours |

Use the **Tier Migration** view (in the app) to move existing chunks between tiers.

### Local database

The local metadata database is `backup.db`. Production builds use **SQLCipher-encrypted SQLite**; the database is unlocked at startup using your password (via the Argon2id-derived key). An older LiteDB-backed format is still supported for migration: if the app finds a legacy `backup.db`, it migrates it to the SQLite format on first unlock with the correct password. The original is preserved as a backup file for manual deletion.

---

## Restore operations

### Single files or selections

1. Open the **Sync** view.
2. In the **Azure Backup** panel, check the files you want.
3. Either tick **Restore to original location** or click **Browse...** for a different destination.
4. Click **Restore Selected**, review the preview dialog, and click **Proceed**.

### Whole folders / mirror sync

1. Select a folder node in the Azure tree.
2. Use the **Remap path** panel to redirect to a target local directory.
3. Click **Mirror Sync** to make the local folder match Azure exactly. Mirror sync **deletes** local files that no longer exist in Azure under that folder, so the preview dialog will show every planned change before anything is touched. Click **Proceed** only after reviewing.

For a guided UX walkthrough see `docs/USER_GUIDE.md`.

---

## File locations

| File | Installed mode | Portable mode |
|---|---|---|
| `backup.db` | `%LOCALAPPDATA%\AzureBackup\backup.db` | `<exe-dir>\backup.db` |
| Argon2id salt | next to `backup.db` | next to `backup.db` |
| Daily logs | `<DataDirectory>\logs\` | `<DataDirectory>\logs\` |
| Per-file `.diag` files (when present) | `<DataDirectory>\diagnostics\<session-id>\` | same |
| Crash log | `<DataDirectory>\` | `<DataDirectory>\` |

`%LOCALAPPDATA%` on non-Windows resolves to the platform's `Environment.SpecialFolder.LocalApplicationData` (e.g. `~/.local/share` on Linux, `~/Library/Application Support` on macOS).

---

## Diagnostics

If something goes wrong, the **Logs** view has an **Export Bundle** button that zips up all logs, `.diag` files, and throughput metrics into a single archive suitable for sharing in a bug report. The bundle exporter excludes the encrypted database and salt files. The **Data Integrity Check** view also has an **Auto-export bundle on failure** option that writes the bundle automatically the first time an integrity-check failure is detected.

The **Logs** view has a **Diagnostic Logging** ON/OFF toggle. This controls runtime opt-in for service-level logging in builds that were compiled with the `DIAGNOSTICLOG` constant defined (Debug builds, by default — see `src/AzureBackup/AzureBackup.csproj` `DefineConstants`). Release builds omit the diagnostic log code entirely; the toggle has no effect there.

---

## Troubleshooting

### "Connection failed"

- Verify the connection string or storage account name.
- Check that outbound HTTPS (port 443) to Azure is allowed by your firewall.
- Ensure the storage account exists and the container is reachable.
- For Entra ID auth, confirm your account has the **Storage Blob Data Contributor** role on the storage account.
- Use **Test Connection** in Settings to isolate auth from sync.

### "Invalid password"

- The password must match exactly what was used during initial setup; it is case-sensitive.
- There is no password recovery. If forgotten, the encrypted backups are unrecoverable by design.
- After 5 failed attempts the app applies a temporary lockout to slow brute-force attempts.

### "File locked" / a file is being skipped

- The backup pipeline waits up to **5 minutes** for a locked file to become readable (`BackupOrchestrator` calls `FileWatcherService.WaitForFileAsync` with a 5-minute timeout). If it remains locked, the file is skipped and will be retried on the next sync cycle.
- Close the application that holds the lock, or add an exclusion pattern for the file.

### Backups appear stuck or slow

- Open the **Logs** view, turn on **Diagnostic Logging**, and re-trigger the operation. The log will show per-chunk timings.
- Check the **Storage Health** view for orphaned chunks consuming space, and the **Data Integrity Check** view for any verified failures.
- For a deeper analysis use the **Export Bundle** button and attach the zip to a bug report.

---

## Security best practices

1. Pick a strong password — at least 16 characters, mixed character classes. The app rejects weak passwords on initial setup.
2. Do not store the password digitally next to the encrypted database. Use a password manager or write it down somewhere physically secure.
3. Treat the Azure connection string as a secret. The app encrypts it inside `backup.db`, but the original copy you paste is your responsibility.
4. Periodically run a real restore (to a temp folder) to verify backups are intact end-to-end.
5. Run the **Data Integrity Check** view periodically. It re-downloads chunks and verifies hashes against the stored metadata.

---

## Technical specifications (verified)

| Component | Value |
|---|---|
| Runtime | .NET 10 |
| GUI framework | Avalonia 11.3.12 |
| MVVM toolkit | CommunityToolkit.Mvvm 8.x |
| Encryption | AES-256-GCM (per chunk) |
| Key derivation | Argon2id, 64 MB memory, 8 lanes, 3 iterations |
| Encryption envelope overhead | 37 bytes per chunk |
| Local database (production) | SQLCipher-encrypted SQLite (`SQLitePCLRaw.bundle_e_sqlcipher` 2.1.x, `Microsoft.Data.Sqlite` 10.x) |
| Local database (legacy / migration source) | LiteDB 5.x |
| Chunking | Content-defined, Rabin-style rolling hash (window 48, prime 31), per-extension config |
| Default file-level concurrency | 16 (`MaxParallelFileBackups`, raised from 8 in B27 based on `TwoTierFileSplitBigScaleBenchmark`) |
| Default chunk-level concurrency per file | 6 (`MaxParallelChunkUploads`) |
| Default `MemoryLimitEnabled` | `true` (raised from `false` in B27) |
| Default `MemoryLimitMB` | hardware-aware: `min(round_down_to_step(0.25 * total_physical_RAM), 8192 MB)` (B29; was a flat `8192` from B27, was `2048` pre-B27). Existing user databases keep whatever value they previously stored; the rule only applies on fresh installs. See `SystemMemoryHelper.GetRecommendedDefaultLimitMB`. |
| Memory-budget enforcement | Producer-side charging (B30): `ChunkingService.ChunkAndStreamChangedAsync` calls `MemoryBudget.AcquireAsync` BEFORE allocating each chunk's payload buffer, so the budget acts as a back-pressure gate on the producer. The consumer mirrors the producer's accounting decision via `ChunkPayload.ChargedBytes` and `ChunkPayload.ReturnToPool`. Chunks at or above `ChunkingService.PoolSkipThresholdBytes` (16 MB) bypass `ArrayPool<byte>.Shared` and use exact `byte[]` allocations to keep the budget vs working-set delta bounded (B33). The deadlock-avoidance branch in `MemoryBudget.AcquireAsync` only fires for genuinely oversized requests (`bytes > totalBytes && _usedBytes == 0`); each such admission increments `MemoryBudget.OversizedAdmissions` (B34). Per-operation `BackupMemoryReporter` emits a structured memory line through `StatusChanged` every 30 seconds (B36); the line carries budget usage, stall delta, oversized-admission delta, GC heap, GC memory load, working set, private memory, and the unaccounted-for delta (working set minus budget used). |
| Azure SDK | `Azure.Storage.Blobs` (see `src/AzureBackup.Core/AzureBackup.Core.csproj` for current pinned version) |

If you need exact version numbers, check the `.csproj` files; this table is updated when those references change but the `.csproj` files are the source of truth.

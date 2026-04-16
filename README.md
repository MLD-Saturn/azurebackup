# Azure Backup Tool

A zero-knowledge encrypted backup tool that syncs your files to Microsoft Azure Blob Storage with client-side encryption. Built with [Avalonia UI](https://avaloniaui.net/) and .NET 10.

## Features

- **Zero-Knowledge Encryption** -- All data is encrypted locally using AES-256-GCM before upload. Your password never leaves your machine.
- **Delta Sync** -- Content-defined chunking (CDC) uploads only changed portions of files, minimizing bandwidth and storage costs.
- **Real-Time Monitoring** -- File system watcher detects changes in watched folders and backs up automatically.
- **Operation Previews** -- Review exactly what will happen before any backup, restore, or sync operation runs.
- **Two-Way Sync** -- Side-by-side view of local and Azure files with mirror sync, selective restore, and conflict detection.
- **Storage Health** -- Chunk index management, orphan detection, deduplication statistics, and per-tier breakdowns (Hot, Cool, Cold).
- **Flexible Authentication** -- Supports both Microsoft Entra ID (work/school accounts) and Connection String (personal accounts).
- **Easy Restore** -- Restore individual files, search by name, or perform full recovery with hash verification.
- **Parallel Transfers** -- Concurrent chunk uploads and downloads for maximum bandwidth utilization.
- **Diagnostic Logging** -- Optional detailed service-level logging for troubleshooting.
- **Tree and Flat Views** -- Browse backed-up files as a folder tree or a flat searchable list.
- **Cross-Platform** -- Runs on Windows, macOS (Intel and Apple Silicon), and Linux.
- **Portable or Installed** -- Ship as a self-contained single executable on a USB drive (portable mode) or install conventionally with data in `LocalAppData`.

## Quick Start

### 1. Create an Azure Storage Account

Follow the [Azure Setup Guide](docs/SETUP.md) to create a storage account and obtain a connection string (or configure Entra ID access).

### 2. Get the Application

```bash
# Build from source (requires .NET 10 SDK)
dotnet build

# Or use the build scripts
./build-portable.cmd          # Windows portable (single exe + marker file)
./build-portable.sh           # All platforms portable (win-x64, linux-x64, osx-x64, osx-arm64)
./build-installed.cmd         # Windows installed (data in %LocalAppData%\AzureBackup)
./build-installed.sh          # All platforms installed
```

### 3. First-Time Setup

1. **Launch the application** -- you will be prompted to create a password.
2. **Choose an authentication method**:
   - *Connection String* -- paste the string from Azure Portal > Storage Account > Access Keys.
   - *Microsoft Entra ID* -- click "Sign in with Microsoft" and enter your storage account name.
3. **Set your encryption password** and confirm it.
   > **CRITICAL**: This password cannot be recovered. Store it safely!
4. **Add folders to watch** and optionally configure exclusion patterns (e.g. `*.tmp;*.log;node_modules;.git`).
5. Click **Initialize & Connect**.

### 4. Start Backup

1. Go to the **Dashboard**.
2. Click **Start Monitoring** -- the app scans your folders and begins backing up.
3. Changed files are detected automatically and uploaded in the background.

For detailed instructions see the **[User Guide](docs/USER_GUIDE.md)**.

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json` for the exact version)

### Build and Run

```bash
git clone https://github.com/MLD-Saturn/azurebackup.git
cd azurebackup

# Build the solution
dotnet build

# Run the application in development
dotnet run --project src/AzureBackup

# Run the test suite
dotnet test
```

### Publish

```bash
# Portable single-file executable (Windows x64)
dotnet publish src/AzureBackup -c Release -r win-x64 --self-contained true -o publish/portable

# macOS Apple Silicon
dotnet publish src/AzureBackup -c Release -r osx-arm64 --self-contained true -o publish/portable

# Linux x64
dotnet publish src/AzureBackup -c Release -r linux-x64 --self-contained true -o publish/portable
```

To run in **portable mode**, place a `portable.marker` file next to the executable. The database and configuration will be stored in the same directory (ideal for USB drives). Without the marker, data is stored in the platform's local app data folder.

## Project Structure

```
src/
  AzureBackup/            Avalonia UI application (MVVM, CommunityToolkit.Mvvm)
  AzureBackup.Core/       Core library -- encryption, chunking, blob storage, database, restore
tests/
  AzureBackup.Tests/      Unit and integration tests (xUnit)
docs/
  SETUP.md                Azure resource setup instructions
  USER_GUIDE.md           End-user guide
```

## Architecture

```
+----------------------------------------------+
|             Your Computer                    |
|  +----------------------------------------+  |
|  |         Azure Backup Tool              |  |
|  |                                        |  |
|  |  [File Watcher] -> [Chunking (CDC)]    |  |
|  |         |              |               |  |
|  |  [AES-256-GCM] <- Argon2id Derived Key |  |
|  |         |                              |  |
|  |  [Azure Blob Service] (parallel I/O)   |  |
|  +----------------------------------------+  |
|                    |                         |
+----------------------------------------------+
                     | HTTPS (TLS 1.3)
+----------------------------------------------+
|           Azure Blob Storage                 |
|  +----------------------------------------+  |
|  |   Encrypted Blobs (unreadable)        |  |
|  |   - chunks/<SHA-256 hash>             |  |
|  |   - metadata/<path hash>              |  |
|  |   - index/chunk-index-backup.enc      |  |
|  +----------------------------------------+  |
+----------------------------------------------+
```

## Security

### Encryption

- **Key Derivation**: Argon2id -- 64 MB memory, 3 iterations, 8-way parallelism
- **Encryption**: AES-256-GCM with a random 96-bit nonce per chunk
- **Integrity**: CRC32 checksum + GCM authentication tag on every encrypted blob
- **Format Versioning**: Magic header (`AZBK`) and version byte for forward compatibility

### Zero-Knowledge Architecture

- Azure only sees encrypted blobs with SHA-256 hashed names -- no file names or content are exposed.
- Connection strings are encrypted at rest using your password-derived key.
- No recovery is possible without the password (by design).

### Data Integrity

- SHA-256 content addressing for deduplication and collision resistance
- Chunk sequence validation during restore
- Full file hash verification after restore completes
- Atomic writes via temp files to prevent partial corruption

### Security Hardening

- Rate limiting on password attempts (lockout after 5 failures)
- Exponential backoff on lockouts (15 min, 30 min, 60 min, ...)
- Blob name validation to prevent path traversal
- Restore path validation against sensitive system directories
- Thread-safe encryption key handling with memory zeroing
- Input validation on all public APIs
- Regex timeout protection against ReDoS attacks
- Automatic migration from legacy encryption to Argon2id on unlock

### Reliability

- LiteDB database with encrypted storage and transactional writes
- FileSystemWatcher buffer overflow recovery with automatic rescan
- Upload retry with exponential backoff on integrity failures (up to 25 retries)
- Proper error handling with custom exception types (`DataIntegrityException`, `SecurityPolicyException`, `InvalidPasswordException`)

## Documentation

- [Azure Setup Guide](docs/SETUP.md) -- create a storage account and configure access
- [User Guide](docs/USER_GUIDE.md) -- daily usage, restore, sync, and troubleshooting

## Benchmarks

Local-developer micro-benchmarks live in `benchmarks/AzureBackup.Benchmarks/` and use
[BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet). They are **not** part of
the CI pipeline -- CI runners have unreliable performance characteristics for
micro-benchmarking. Run locally to validate performance changes:

```powershell
# Run every benchmark in the project (Release config is enforced by BDN):
dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks

# Run a single benchmark class:
dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks -- --filter *CdcRollingHash*

# Quick smoke-test run (fewer iterations, ~1 minute total):
dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks -- --job short --warmupCount 2 --iterationCount 5
```

Reports are written to `BenchmarkDotNet.Artifacts/results/` as CSV, Markdown, and HTML.

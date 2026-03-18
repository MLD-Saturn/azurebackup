#!/bin/bash
# ============================================
# Azure Backup Tool - Build Installed Executables (with Diagnostic Logging)
# ============================================
# Same as build-installed.sh but with DIAGNOSTICLOG
# enabled so all diagnostic Log() calls are compiled in.
# ============================================

set -e

echo "Building INSTALLED executables (with diagnostic logging)..."
echo ""

# Clean previous builds
rm -rf publish/installed

# Build for each platform with diagnostic logging enabled
echo "Building for Windows x64..."
dotnet publish src/AzureBackup -c Release -r win-x64 --self-contained true -o publish/installed/win-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for Linux x64..."
dotnet publish src/AzureBackup -c Release -r linux-x64 --self-contained true -o publish/installed/linux-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for macOS x64..."
dotnet publish src/AzureBackup -c Release -r osx-x64 --self-contained true -o publish/installed/osx-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for macOS ARM64 (Apple Silicon)..."
dotnet publish src/AzureBackup -c Release -r osx-arm64 --self-contained true -o publish/installed/osx-arm64 -p:DefineConstants=DIAGNOSTICLOG

# NOTE: No portable.marker file = installed mode

echo ""
echo "============================================"
echo "INSTALLED Build successful! (DIAGNOSTIC LOGGING ENABLED)"
echo "============================================"
echo ""
echo "NOTE: This build includes diagnostic logging."
echo "      Use build-installed.sh for production builds without logging overhead."
echo ""
echo "Output locations:"
echo "  - publish/installed/win-x64/AzureBackup.exe"
echo "  - publish/installed/linux-x64/AzureBackup"
echo "  - publish/installed/osx-x64/AzureBackup"
echo "  - publish/installed/osx-arm64/AzureBackup"
echo ""
echo "Data storage locations by platform:"
echo "  - Windows: %LocalAppData%\\AzureBackup\\backup.db"
echo "  - Linux:   ~/.local/share/AzureBackup/backup.db"
echo "  - macOS:   ~/Library/Application Support/AzureBackup/backup.db"
echo ""
echo "Window title will show: 'Azure Backup - Encrypted Cloud Backup'"
echo ""

# Show file sizes
echo "Executable sizes:"
if [ -f "publish/installed/win-x64/AzureBackup.exe" ]; then
    size=$(du -m "publish/installed/win-x64/AzureBackup.exe" 2>/dev/null | cut -f1)
    echo "  - Windows x64: approximately ${size} MB"
fi
if [ -f "publish/installed/linux-x64/AzureBackup" ]; then
    size=$(du -m "publish/installed/linux-x64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - Linux x64: approximately ${size} MB"
fi
if [ -f "publish/installed/osx-x64/AzureBackup" ]; then
    size=$(du -m "publish/installed/osx-x64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - macOS x64: approximately ${size} MB"
fi
if [ -f "publish/installed/osx-arm64/AzureBackup" ]; then
    size=$(du -m "publish/installed/osx-arm64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - macOS ARM64: approximately ${size} MB"
fi
echo ""
echo "Install the executable to your preferred location."

#!/bin/bash
# ============================================
# Azure Backup Tool - Build Portable Executables (with Diagnostic Logging)
# ============================================
# Same as build-portable.sh but with DIAGNOSTICLOG
# enabled so all diagnostic Log() calls are compiled in.
# ============================================

set -e

echo "Building PORTABLE executables (with diagnostic logging)..."
echo ""

# Clean previous builds
rm -rf publish/portable

# Build for each platform with diagnostic logging enabled
echo "Building for Windows x64..."
dotnet publish src/AzureBackup -c Release -r win-x64 --self-contained true -o publish/portable/win-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for Linux x64..."
dotnet publish src/AzureBackup -c Release -r linux-x64 --self-contained true -o publish/portable/linux-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for macOS x64..."
dotnet publish src/AzureBackup -c Release -r osx-x64 --self-contained true -o publish/portable/osx-x64 -p:DefineConstants=DIAGNOSTICLOG

echo "Building for macOS ARM64 (Apple Silicon)..."
dotnet publish src/AzureBackup -c Release -r osx-arm64 --self-contained true -o publish/portable/osx-arm64 -p:DefineConstants=DIAGNOSTICLOG

# Create portable marker files
echo "Creating portable marker files..."
for dir in publish/portable/*/; do
    echo "This file marks the application as running in portable mode." > "${dir}portable.marker"
    echo "Data will be stored alongside the executable." >> "${dir}portable.marker"
    echo "Delete this file to switch to installed mode." >> "${dir}portable.marker"
done

echo ""
echo "============================================"
echo "PORTABLE Build successful! (DIAGNOSTIC LOGGING ENABLED)"
echo "============================================"
echo ""
echo "NOTE: This build includes diagnostic logging."
echo "      Use build-portable.sh for production builds without logging overhead."
echo ""
echo "Output locations:"
echo "  - publish/portable/win-x64/AzureBackup.exe"
echo "  - publish/portable/linux-x64/AzureBackup"
echo "  - publish/portable/osx-x64/AzureBackup"
echo "  - publish/portable/osx-arm64/AzureBackup"
echo ""
echo "Each folder also contains portable.marker file."
echo "Window title will show: 'Azure Backup - Encrypted Cloud Backup (Portable)'"
echo ""

# Show file sizes
echo "Executable sizes:"
if [ -f "publish/portable/win-x64/AzureBackup.exe" ]; then
    size=$(du -m "publish/portable/win-x64/AzureBackup.exe" 2>/dev/null | cut -f1)
    echo "  - Windows x64: approximately ${size} MB"
fi
if [ -f "publish/portable/linux-x64/AzureBackup" ]; then
    size=$(du -m "publish/portable/linux-x64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - Linux x64: approximately ${size} MB"
fi
if [ -f "publish/portable/osx-x64/AzureBackup" ]; then
    size=$(du -m "publish/portable/osx-x64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - macOS x64: approximately ${size} MB"
fi
if [ -f "publish/portable/osx-arm64/AzureBackup" ]; then
    size=$(du -m "publish/portable/osx-arm64/AzureBackup" 2>/dev/null | cut -f1)
    echo "  - macOS ARM64: approximately ${size} MB"
fi
echo ""
echo "Copy the appropriate folder contents to your USB device."

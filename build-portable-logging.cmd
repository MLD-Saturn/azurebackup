@echo off
setlocal EnableDelayedExpansion
REM ============================================
REM Azure Backup Tool - Build Portable Executable (with Diagnostic Logging)
REM ============================================
REM Same as build-portable.cmd but with DIAGNOSTICLOG
REM enabled so all diagnostic Log() calls are compiled in.
REM ============================================

echo Building PORTABLE executable for Windows x64 (with diagnostic logging)...
echo.

REM Check if the exe is running
tasklist /FI "IMAGENAME eq AzureBackup.exe" 2>NUL | find /I /N "AzureBackup.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo ERROR: AzureBackup.exe is currently running.
    echo Please close the application and try again.
    echo.
    pause
    exit /b 1
)

REM Clean previous builds (with retry for locked files)
if exist "publish\portable" (
    echo Cleaning previous build...
    rmdir /s /q "publish\portable" 2>NUL
    if exist "publish\portable" (
        echo.
        echo ERROR: Cannot delete publish folder - files may be in use.
        echo Please close any applications using files in that folder.
        echo.
        pause
        exit /b 1
    )
)

REM Build and publish with diagnostic logging enabled
dotnet publish src\AzureBackup -c Release -r win-x64 --self-contained true -o publish\portable -p:DefineConstants=DIAGNOSTICLOG

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build failed!
    pause
    exit /b 1
)

REM Create portable marker file
echo This file marks the application as running in portable mode. > "publish\portable\portable.marker"
echo Data will be stored alongside the executable. >> "publish\portable\portable.marker"
echo Delete this file to switch to installed mode (data in %%LocalAppData%%\AzureBackup). >> "publish\portable\portable.marker"

echo.
echo ============================================
echo PORTABLE Build successful! (DIAGNOSTIC LOGGING ENABLED)
echo ============================================
echo.
echo Output location: publish\portable\
echo.
echo NOTE: This build includes diagnostic logging.
echo       Use build-portable.cmd for production builds without logging overhead.
echo.
echo Files to copy to USB:
echo   - AzureBackup.exe (the main application)
echo   - portable.marker (keeps it in portable mode)
echo.
echo The database (backup.db) will be created in the 
echo same folder as the executable when you run it.
echo.
echo Window title will show: "Azure Backup - Encrypted Cloud Backup (Portable)"
echo.

REM Show file size
for %%A in (publish\portable\AzureBackup.exe) do (
    set /a size=%%~zA / 1048576
    echo Executable size: approximately !size! MB
)

echo.
pause
endlocal

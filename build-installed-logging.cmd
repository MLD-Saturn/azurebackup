@echo off
setlocal EnableDelayedExpansion
REM ============================================
REM Azure Backup Tool - Build Installed Executable (with Diagnostic Logging)
REM ============================================
REM Same as build-installed.cmd but with DIAGNOSTICLOG
REM enabled so all diagnostic Log() calls are compiled in.
REM ============================================

echo Building INSTALLED executable for Windows x64 (with diagnostic logging)...
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
if exist "publish\installed" (
    echo Cleaning previous build...
    rmdir /s /q "publish\installed" 2>NUL
    if exist "publish\installed" (
        echo.
        echo ERROR: Cannot delete publish folder - files may be in use.
        echo Please close any applications using files in that folder.
        echo.
        pause
        exit /b 1
    )
)

REM Build and publish with diagnostic logging enabled
dotnet publish src\AzureBackup -c Release -r win-x64 --self-contained true -o publish\installed -p:DefineConstants=DIAGNOSTICLOG

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build failed!
    pause
    exit /b 1
)

REM NOTE: No portable.marker file = installed mode

echo.
echo ============================================
echo INSTALLED Build successful! (DIAGNOSTIC LOGGING ENABLED)
echo ============================================
echo.
echo Output location: publish\installed\
echo.
echo NOTE: This build includes diagnostic logging.
echo       Use build-installed.cmd for production builds without logging overhead.
echo.
echo Installation:
echo   Copy AzureBackup.exe to any location (e.g., C:\Program Files\AzureBackup)
echo.
echo Data storage:
echo   Database and settings will be stored in:
echo   %%LocalAppData%%\AzureBackup\backup.db
echo.
echo Window title will show: "Azure Backup - Encrypted Cloud Backup"
echo.

REM Show file size
for %%A in (publish\installed\AzureBackup.exe) do (
    set /a size=%%~zA / 1048576
    echo Executable size: approximately !size! MB
)

echo.
pause
endlocal

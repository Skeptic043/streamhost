@echo off
setlocal
REM Spectari one-time setup. Usage: setup.bat [port]   (default 8093)
REM
REM This is the manual fallback for the app's "Open port" button. It just
REM elevates and hands the work to Spectari.exe, which reserves the stream
REM URL for this user and opens the firewall to Tailscale (100.64.0.0/10).
REM
REM LAN viewers are not offered here. Turn that on inside the app: tick
REM "Allow LAN viewers" next to "Open port", then click Open port.
REM
REM Right-click this file and select "Run as administrator".

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo   Administrator rights required.
    echo   Right-click setup.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

set "PORT=%~1"
if "%PORT%"=="" set "PORT=8093"

set "EXE=%~dp0Spectari.exe"
if not exist "%EXE%" (
    echo.
    echo   Could not find Spectari.exe next to this script.
    echo   Keep setup.bat in the same folder as Spectari.exe and run it there.
    echo.
    pause
    exit /b 1
)

echo.
echo Opening port %PORT% for Spectari so viewers on Tailscale can connect...
echo.

REM Spectari.exe is a GUI-subsystem program, so cmd would not wait for it on
REM its own. "start /wait" waits for it and reports its exit code in ERRORLEVEL.
REM --setup-confirm makes it ask before replacing a reservation owned by someone
REM else. Capture the code right away, before anything else touches ERRORLEVEL.
start "" /wait "%EXE%" --setup-port %PORT% --setup-confirm
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo   Setup complete for port %PORT%. Viewers on Tailscale can now connect.
    echo   Re-running this is safe. For a different port: setup.bat 8094
) else if "%RC%"=="4" (
    echo   No change made. The existing URL reservation for port %PORT% was kept.
    echo   Pick a different port instead: setup.bat 8094
) else if "%RC%"=="2" (
    echo   FAILED: could not reserve the stream URL for port %PORT%.
    echo   Include this window's text when reporting.
) else if "%RC%"=="3" (
    echo   FAILED: could not add the firewall rule for port %PORT%.
    echo   Include this window's text when reporting.
) else (
    echo   FAILED: port setup did not complete. Exit code %RC%.
    echo   Include this window's text when reporting.
)
echo.
pause

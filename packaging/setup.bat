@echo off
REM StreamHost one-time setup. Usage: setup.bat [port]   (default 8093)
REM StreamHost serves the stream to viewers' browsers on this port; this
REM script lets Windows accept those incoming connections. It reserves the
REM stream URL for this user and adds a firewall rule scoped to Tailscale
REM (100.64.0.0/10) by default. It can also cover your private LAN address
REM ranges if you answer yes to the LAN prompt below.
REM Right-click this file and select "Run as administrator".
REM This is the manual fallback for the app's "Fix access" button.

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo   Administrator rights required.
    echo   Right-click setup.bat and select "Run as administrator".
    echo.
    pause
    exit /b 1
)

set PORT=%~1
if "%PORT%"=="" set PORT=8093

echo This opens the port StreamHost uses to serve your stream to viewers'
echo browsers, so friends on Tailscale (and, if you allow it, your LAN)
echo can connect to it.

REM If some OTHER software already reserved this port's URL, don't silently
REM delete it. Show it and ask.
netsh http show urlacl url=http://+:%PORT%/ | findstr /i "Reserved" >nul 2>&1
if %errorlevel% equ 0 (
    echo An existing URL reservation was found for port %PORT%:
    netsh http show urlacl url=http://+:%PORT%/ | findstr /i "Reserved User"
    choice /m "Replace it with StreamHost's reservation"
    if errorlevel 2 (
        echo   Keeping the existing reservation. Pick a different port instead.
        pause
        exit /b 1
    )
)

REM Firewall scope. Tailscale-only is the secure default; opt in to the LAN
REM ranges only if asked for.
set RANGES=100.64.0.0/10
choice /m "Allow local network (LAN) viewers in addition to Tailscale"
if not errorlevel 2 set RANGES=100.64.0.0/10,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12

echo Reserving http://+:%PORT%/ for %USERNAME%...
netsh http delete urlacl url=http://+:%PORT%/ >nul 2>&1
netsh http add urlacl url=http://+:%PORT%/ user="%USERDOMAIN%\%USERNAME%" >nul
if %errorlevel% neq 0 (
    echo   FAILED: could not reserve the URL. Include this window's text when reporting.
    pause
    exit /b 1
)

echo Adding firewall rule for port %PORT% (allowed ranges: %RANGES%)...
netsh advfirewall firewall delete rule name="StreamHost %PORT%" >nul 2>&1
netsh advfirewall firewall add rule name="StreamHost %PORT%" dir=in action=allow protocol=TCP localport=%PORT% remoteip=%RANGES% >nul
if %errorlevel% neq 0 (
    echo   FAILED: could not add the firewall rule. Include this window's text when reporting.
    pause
    exit /b 1
)

echo.
echo   Setup complete for port %PORT%. Both steps verified.
echo   Re-running this script is safe; it replaces its own entries.
echo   To use a different port: setup.bat 8094
echo.
pause

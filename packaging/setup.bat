@echo off
REM StreamHost one-time setup. Usage: setup.bat [port]   (default 8093)
REM Reserves the stream URL for this user and adds a firewall rule scoped to
REM Tailscale (100.64.0.0/10) and private LAN address ranges.
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

set PORT=%~1
if "%PORT%"=="" set PORT=8093

REM If some OTHER software already reserved this port's URL, don't silently
REM delete it — show it and ask.
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

echo Reserving http://+:%PORT%/ for %USERNAME%...
netsh http delete urlacl url=http://+:%PORT%/ >nul 2>&1
netsh http add urlacl url=http://+:%PORT%/ user="%USERDOMAIN%\%USERNAME%" >nul
if %errorlevel% neq 0 (
    echo   FAILED: could not reserve the URL. Include this window's text when reporting.
    pause
    exit /b 1
)

echo Adding firewall rule for port %PORT% (private address ranges only)...
netsh advfirewall firewall delete rule name="StreamHost %PORT%" >nul 2>&1
netsh advfirewall firewall add rule name="StreamHost %PORT%" dir=in action=allow protocol=TCP localport=%PORT% remoteip=100.64.0.0/10,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12 >nul
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

@echo off
cd /d "%~dp0"
REM Command-line alternative to the app window (double-click StreamHost.exe
REM for the normal experience — this file is optional).
REM
REM PORT must match what setup.bat was run for (default 8093).
REM Edit the window title to match your game; part of the title is enough.
REM Run StreamHost.exe --list-windows to see what's capturable.
REM Whole monitor instead: replace --window "..." with --monitor 0
REM Weak upload? Lower --bitrate to 8000.

set PORT=8093

StreamHost.exe --window "Path of Exile 2" --fps 60 --bitrate 12000 --port %PORT%
pause

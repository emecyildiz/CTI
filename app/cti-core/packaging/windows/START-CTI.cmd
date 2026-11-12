@echo off
setlocal
"%~dp0CTI-Core.exe" --open-browser
if errorlevel 1 (
    echo.
    echo CTI did not start. See the message above, then press a key.
    pause >nul
)

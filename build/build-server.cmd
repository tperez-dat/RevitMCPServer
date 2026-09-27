@echo off
REM Publishes the MCP server for an MCP client to launch.
REM Double-click this, or run it from any terminal.

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-server.ps1" %*
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% NEQ 0 (
    echo Publish FAILED with exit code %EXITCODE%. Read the message above.
) else (
    echo Publish finished.
)

echo.
pause
exit /b %EXITCODE%

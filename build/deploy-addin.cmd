@echo off
REM Installs the Revit MCP Bridge add-in.
REM Double-click this, or run it from any terminal. Close Revit first.
REM
REM Wraps the PowerShell script with -ExecutionPolicy Bypass so a managed machine's
REM script restrictions do not block it. Nothing about the machine's settings is changed.

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-addin.ps1" %*
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% NEQ 0 (
    echo Install FAILED with exit code %EXITCODE%. Read the message above.
) else (
    echo Install finished.
)

REM Keep the window open when this was double-clicked rather than run from a prompt.
echo.
pause
exit /b %EXITCODE%

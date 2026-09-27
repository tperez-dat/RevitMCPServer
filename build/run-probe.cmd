@echo off
REM Checks a running Revit MCP bridge against the open model.
REM Revit must be running with a model open. Read checks never modify the model.
REM
REM Pass -Writes to also run the self-cleaning write check:
REM   run-probe.cmd -Writes

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-probe.ps1" %*
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% NEQ 0 (
    echo Some checks FAILED. See the lines above.
) else (
    echo All checks passed.
)

echo.
pause
exit /b %EXITCODE%

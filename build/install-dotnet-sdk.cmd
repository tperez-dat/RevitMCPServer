@echo off
REM Installs a .NET SDK for the current user only - no administrator rights needed.
REM
REM Double-click for .NET 10 (Revit 2027), or from a terminal:
REM   install-dotnet-sdk.cmd -Channel 8.0

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-dotnet-sdk.ps1" %*
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE% NEQ 0 (
    echo SDK install FAILED with exit code %EXITCODE%. Read the message above.
) else (
    echo SDK install finished.
)

echo.
pause
exit /b %EXITCODE%

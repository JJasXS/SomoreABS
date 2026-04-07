@echo off
setlocal EnableExtensions
cd /d "%~dp0"

REM Prefer SDK "dotnet run" so the right shared runtime is used even if you only have dotnet on PATH.
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: "dotnet" was not found. Install the .NET SDK or .NET Runtime 8+ from https://dotnet.microsoft.com/download
  echo Or use Publish-SelfContained.cmd and run the exe from the publish folder.
  pause
  exit /b 1
)

dotnet run -c Release --project "%~dp0ABS_System.MachineIdHelper.csproj" -- %*
set EXITCODE=%ERRORLEVEL%
echo.
if %EXITCODE% neq 0 echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%

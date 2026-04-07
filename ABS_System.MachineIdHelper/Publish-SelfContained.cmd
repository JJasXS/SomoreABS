@echo off
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo Install .NET SDK from https://dotnet.microsoft.com/download
  pause
  exit /b 1
)

set OUT=%~dp0publish\win-x64-selfcontained
echo Publishing to: %OUT%
dotnet publish "%~dp0ABS_System.MachineIdHelper.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%"
if errorlevel 1 (
  pause
  exit /b 1
)

echo.
echo Done. Run:  "%OUT%\ABS_MachineIdHelper.exe"
echo Optional:  "%OUT%\ABS_MachineIdHelper.exe" --clipboard
pause

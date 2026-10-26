@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%bin\Debug\net10.0-windows10.0.26100.0\win-x64\AgentDock.exe"
pushd "%ROOT%" || exit /b 1

echo Stopping running Agent Dock instances...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-Process AgentDock -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --quit }; Get-Process AgentDock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue"

set "PWSH="
if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe" set "PWSH=%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe"
if not defined PWSH if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined PWSH for /f "delims=" %%P in ('where.exe pwsh.exe 2^>nul') do if not defined PWSH set "PWSH=%%P"
if not defined PWSH goto :missing_pwsh

echo Building taskbar icon assets...
"%PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\Build-AppIcons.ps1"
if errorlevel 1 goto :failed

echo Building AgentDock.csproj...
dotnet build "%ROOT%AgentDock.csproj" -nologo -v q
if errorlevel 1 goto :failed

if not exist "%EXE%" goto :missing_exe

echo Starting the local Debug build in the background...
start "" "%EXE%" --background
popd
exit /b 0

:missing_pwsh
echo PowerShell 7 was not found. Add pwsh.exe to PATH and try again.
goto :failed

:missing_exe
echo Build completed, but the expected executable was not found:
echo %EXE%

:failed
echo Local build did not complete. See the error above.
popd
exit /b 1

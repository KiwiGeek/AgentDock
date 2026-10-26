# Local development

Run these commands from the repository root in PowerShell 7 (`pwsh`). The icon builder uses .NET APIs not available in Windows PowerShell 5.1. The batch file `local-build.bat` runs the same sequence: stop Agent Dock, rebuild taskbar icon assets, build the project, and start the local Debug copy in the background. It locates `pwsh.exe` through PowerShell's command lookup, including Windows App execution aliases.

## Build taskbar icon assets

When adding or changing a taskbar icon source in `Assets/AppIcons/appicon-<id>.png`, regenerate the compiled `.ico` files:

```powershell
.\tools\Build-AppIcons.ps1
```

The script writes `Assets/AppIcons/ico/<id>.ico`. Commit the generated `.ico` files along with the source changes. Agent icons and hover backgrounds under `Assets/Agents` are compiled directly into the app and do not need a separate asset build.

## Stop Agent Dock

Ask each running copy to quit gracefully, then force-stop any copy that remains (for example, if it is unresponsive):

```powershell
Get-Process AgentDock -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --quit }
Get-Process AgentDock -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Build and launch locally

Build the project file, not the repository folder: building the folder can select the solution's ARM64 platform. This Debug build uses the project's default `win-x64` runtime identifier.

```powershell
$root = (Get-Location).Path
$exe = Join-Path $root 'bin/Debug/net10.0-windows10.0.26100.0/win-x64/AgentDock.exe'

# Stop the old copy before building so the executable is not locked.
Get-Process AgentDock -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --quit }
Get-Process AgentDock -ErrorAction SilentlyContinue | Stop-Process -Force

& "$root/tools/Build-AppIcons.ps1" -Root $root
if ($LASTEXITCODE -ne 0) { throw 'Building taskbar icon assets failed.' }

dotnet build "$root/AgentDock.csproj" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building Agent Dock failed.' }

Start-Process $exe -ArgumentList '--background'
```

The background launch keeps the resident instance ready without opening the flyout. To show the local build's flyout, run the exe with no arguments:

```powershell
& $exe
```

The second launch hands off to the resident copy. To stop it later, run the exe with `--quit` or use the Stop Agent Dock commands above.

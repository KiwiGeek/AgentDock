<#
.SYNOPSIS
    Stops the running Agent Dock, optionally rebuilds the taskbar icon assets, builds the Debug
    exe, and launches it in the background.

.DESCRIPTION
    The running copy is quit first: it holds a lock on the exe the build writes to, and --quit
    releases the single-instance mutex and stops hosted agents the tidy way. Anything left after
    that is killed, so a hung resident can't block the build either.

    The build goes through the .csproj, never the folder: a bare `dotnet build` picks up
    AgentDock.slnx, whose default platform is ARM64, and fails with NETSDK1032.

    A successful build starts the new exe with --background, so it waits for a taskbar click
    instead of opening a flyout nobody asked for. Run the exe with no arguments to open it.

.PARAMETER Assets
    Rebuild the taskbar icon assets (src\AgentDock\Assets\AppIcons\appicon-<id>.png into its ico\)
    without asking.

.PARAMETER NoAssets
    Skip the taskbar icon assets without asking.

.EXAMPLE
    pwsh -File tools\Build-And-Run.ps1
#>
[CmdletBinding()]
param(
    [switch]$Assets,
    [switch]$NoAssets
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\AgentDock\AgentDock.csproj'
$exe = Join-Path $root 'src\AgentDock\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AgentDock.exe'

if ($Assets -and $NoAssets) { throw 'Pass either -Assets or -NoAssets, not both.' }

# An instance holding the exe makes the build fail on a locked file (MSB3026/MSB3027), so it has to
# be gone before every build, not just at the start: clicking the pinned icon while the assets build
# starts a new resident, and the exe stays locked for a moment after a force-kill.
function Stop-AgentDock {
    Get-Process AgentDock -ErrorAction SilentlyContinue | ForEach-Object {
        try { Start-Process -FilePath $_.Path -ArgumentList '--quit' -Wait -ErrorAction Stop } catch { }
    }
    Get-Process AgentDock -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 30 -and (Get-Process AgentDock -ErrorAction SilentlyContinue); $i++) {
        Start-Sleep -Milliseconds 100
    }
}

$rebuildAssets = $Assets.IsPresent
if (-not $Assets -and -not $NoAssets) {
    $rebuildAssets = (Read-Host 'Rebuild the taskbar icon assets? [y/N]') -match '^(y|yes)$'
}

Write-Host 'Stopping Agent Dock'
Stop-AgentDock

if ($rebuildAssets) {
    Write-Host 'Rebuilding the taskbar icon assets'
    try { & (Join-Path $root 'tools\Build-AppIcons.ps1') } catch { throw "Building the assets failed: $($_.Exception.Message)" }
}

Write-Host 'Building'
Stop-AgentDock
dotnet build $project -nologo -v q
if ($LASTEXITCODE -ne 0) {
    # Most likely a copy opened during the build. Worth one more try now that it's stopped.
    Stop-AgentDock
    dotnet build $project -nologo -v q
}
if ($LASTEXITCODE -ne 0) { throw 'The build failed, so nothing was launched.' }
if (-not (Test-Path $exe)) { throw "The build finished but the exe isn't where expected: $exe" }

Start-Process $exe -ArgumentList '--background'
Write-Host "Agent Dock is running in the background: $exe"
Write-Host 'Click the taskbar icon to open it, or run the exe with no arguments.'

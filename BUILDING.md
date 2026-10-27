# Building Agent Dock locally

The dev loop: rebuild the taskbar icon assets if they changed, build the Debug exe, and launch it.
`tools\Build-And-Run.ps1` does all three.

## The one-step way

```powershell
pwsh -File tools\Build-And-Run.ps1
```

It asks whether to rebuild the taskbar icon assets, quits the running copy (the build can't
overwrite an exe that's in use), builds `src\AgentDock\AgentDock.csproj`, and starts the new exe with
`--background`. Answer `n` to the question to go straight to the build, or pass `-Assets` /
`-NoAssets` to skip the question. Run it with PowerShell 7 (`pwsh`): the asset script uses
`Math.Clamp`, which Windows PowerShell 5.1's compiler doesn't have.

```powershell
pwsh -File tools\Build-And-Run.ps1 -NoAssets    # just build and launch
```

Click the pinned taskbar icon to open the flyout, or run the exe with no arguments. A second run
within 600 ms of a hide counts as a toggle, so it can close what it just opened.

## What "the assets" are

There are two kinds, and only one has a separate build step:

- **Taskbar icon choices.** `Assets\AppIcons\appicon-<id>.png` are 1024 px sources that don't
  ship. `tools\Build-AppIcons.ps1` crops each to what's drawn and writes
  `Assets\AppIcons\ico\<id>.ico` with frames at 16, 20, 24, 32, 40, 48, 64 and 256 px. Those
  `.ico` files are compiled in (`AppIcons.<id>.ico`). Rerun the script after adding or changing a
  source picture, and commit the `.ico` files it writes. On this machine the images come out the
  same but the bytes differ from what's committed (a newer .NET's PNG encoder), so a rebuild with
  nothing changed still shows most of the `.ico` files as modified; `git checkout -- Assets\AppIcons\ico`
  puts them back.
- **Agent icons and hover pictures.** `Assets\Agents\<id>.png` and `<id>-background.png` are
  embedded straight from their files, so changing one only needs the normal build. Keep icons at
  256 px or less.

## By hand

```powershell
# 1. Stop the running copy. --quit releases the single-instance mutex and stops hosted agents.
Get-Process AgentDock -ErrorAction SilentlyContinue |
    ForEach-Object { Start-Process $_.Path -ArgumentList '--quit' -Wait }

# 2. Optional: rebuild the taskbar icon assets.
.\tools\Build-AppIcons.ps1

# 3. Build the project by path. The repo root has no project of its own.
dotnet build src\AgentDock\AgentDock.csproj -nologo -v q

# 4. Launch the result in the background.
Start-Process .\src\AgentDock\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AgentDock.exe -ArgumentList '--background'
```

## Where things are

| What | Where |
| --- | --- |
| Debug exe | `bin\Debug\net10.0-windows10.0.26100.0\win-x64\AgentDock.exe` |
| Settings and agents | `%APPDATA%\AgentDock.Dev\agents.json` |
| App-managed state | `%APPDATA%\AgentDock.Dev\state.json` |
| Cached icons | `%LOCALAPPDATA%\AgentDock.Dev\Icons\` |
| Usage (shared with the installed copy) | `%LOCALAPPDATA%\AgentDock\*-usage.json` |

A Debug build is `AgentDock.Dev`: its own instance, pin, flyout and settings, so it runs beside an
installed Agent Dock (which uses `AgentDock` without `.Dev`). Click the Debug pin for your build, and
the installed one for the release.

No config reload is needed: every show re-reads `agents.json` and `state.json`. Debug builds
skip the taskbar pin prompt unless `AGENTDOCK_PIN_CHECK=1` is set.

## When something goes wrong

- **NETSDK1032:** you built the folder (which found `AgentDock.slnx`) instead of the `.csproj`.
- **WMC0909 "Cannot resolve DataType" or WMC1509 "No LocalAssembly":** usually a C# compile
  error behind them. Fix that error first; the XAML ones go away with it.
- **The build fails on a locked exe:** Agent Dock is still running. Quit it, or let the script's
  kill loop do it.
- **Claude Code's status line** runs the Debug exe on every refresh, so while it points there a
  build can hit a short lock (MSBuild retries) and the status line can be blank for a moment.
- **`dotnet publish -c Release -r win-x64`** (the shipping build) needs the MSVC linker from
  Visual Studio's "Desktop development with C++" workload. A Debug build doesn't.

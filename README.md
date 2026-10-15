# Agent Dock

A Windows 11 taskbar flyout for launching AI agents, built with WinUI 3. Click the pinned icon
and an acrylic flyout opens above it; pick an agent and it starts. CLI agents open in Windows
Terminal in the working folder you've chosen. Desktop agents just launch.

Right-clicking the taskbar icon gives the same list as a jump list, which launches straight into
the last folder you used without opening the flyout.

## Requirements

- Windows 11
- .NET 10 SDK
- Visual Studio's **Desktop development with C++** workload (or the standalone C++ build tools).
  Native AOT uses the MSVC linker. If you'd rather skip it, set `<PublishAot>false</PublishAot>`
  in the project file; everything still works, the hand-off is just a little slower.

The Windows App SDK runtime is bundled into the output, so nothing else needs installing.

## Build and pin

```powershell
dotnet publish -c Release -r win-x64 -o C:\Tools\AgentDock
```

Use `-r win-arm64` on an ARM PC. Then run `C:\Tools\AgentDock\AgentDock.exe`. If it isn't pinned
yet, it asks you to pin it: right-click its icon on the taskbar and choose **Pin to taskbar**, and
the window closes by itself. (Windows doesn't let apps pin themselves, so this step is yours.)
Choosing **Quit** instead closes Agent Dock and removes its sign-in entry, since it can't be opened
without the pin. It asks again whenever it starts unpinned.

You can also pin it before running it: in File Explorer, right-click the exe, choose
**Show more options**, then **Pin to taskbar**.

### Upgrading from the WPF version

Quit the old one first (right-click its taskbar icon → **Quit Agent Dock**, or run the old exe
with `--quit`). `agents.json`, recent folders and cached icons carry over unchanged. If you publish
to the same folder, the existing pin and sign-in entry keep working; otherwise unpin the old one.

## Using it

| Action | Result |
| --- | --- |
| Click an agent, or press its number (1–9) | Launches it and closes the flyout |
| Arrow keys, Enter | Move through the list and launch |
| Folder drop-down / folder button | Pick a recent folder or browse for a new one |
| Terminal button (footer) | Plain shell in the working folder, no agent |
| Pencil button (footer) | Opens `agents.json` |
| Esc, click elsewhere, or click the taskbar icon again | Closes the flyout |

CLI agents run inside a shell (`pwsh -NoExit -Command claude`, for example), so npm `.cmd`
shims work and you land back at a prompt in the folder when the agent exits.

## Why it opens quickly

Agent Dock stays running in the background with the flyout already built and rendered once
off-screen. Clicking the pinned icon starts a second copy of the exe, which never initialises
WinUI: it sends one line over a named pipe to the running instance and exits. Because the exe is
compiled with Native AOT, that copy starts in milliseconds. The running instance just un-hides
its window.

It registers itself to start hidden when you sign in (`HKCU\...\Run`, `--background`), so even
the first click of the day is fast. Turn that off with `"startWithWindows": false`.

### Quitting (needed before re-publishing over the exe)

Right-click the taskbar icon and choose **Quit Agent Dock**, or run `AgentDock.exe --quit`.
The exe is locked while the background instance is running. If Agent Dock has stopped responding,
Quit force-closes it after about three seconds. The next click starts it from cold, which takes a
moment longer.

## Configuration

`%APPDATA%\AgentDock\agents.json` is created on first run. Comments and trailing commas are allowed.
Agents that aren't installed are hidden unless you set `hideUnavailable` to `false`.

```jsonc
{
  "terminal": "auto",        // auto (Windows Terminal if present) | wt | console
  "shell": "auto",           // auto (pwsh, else Windows PowerShell) | pwsh | powershell | cmd
  "hideUnavailable": true,
  "maxRecentFolders": 8,
  "startWithWindows": true,  // start hidden at sign-in so the first click is fast
  "agents": [
    // CLI: resolved against PATH
    { "id": "claude", "name": "Claude Code", "command": "claude" },
    { "id": "claude-resume", "name": "Claude Code (continue)", "command": "claude", "arguments": "--continue" },
    { "id": "codex-cmd", "name": "Codex", "command": "codex", "shell": "cmd" },

    // Desktop: found by Start menu name...
    { "id": "claude-desktop", "name": "Claude", "kind": "desktop", "startMenuNames": ["Claude"] },
    // ...or by explicit exe path...
    { "id": "lmstudio", "name": "LM Studio", "kind": "desktop", "path": "%LOCALAPPDATA%\\Programs\\LM Studio\\LM Studio.exe" },
    // ...or by AppUserModelID (run Get-StartApps in PowerShell to list them)
    { "id": "something", "name": "Something", "kind": "desktop", "appId": "Publisher.App_xxxx!App" }
  ]
}
```

Changes take effect the next time the flyout opens; no restart needed. Ids must be unique;
the jump list uses them.

`state.json` in the same folder holds recent folders and cached app ids. Icons are cached under
`%LOCALAPPDATA%\AgentDock\Icons`; delete that folder if an app's icon changes.

## How it's put together

- **WinUI 3, unpackaged and self-contained.** Controls, light/dark mode and accent colour all
  come from Windows itself. The flyout is a borderless, always-on-top `AppWindow` with a
  `DesktopAcrylicBackdrop`, hidden from the taskbar and Alt+Tab.
- **Placement** reads the cursor position and the monitor's work area, so the flyout sits above
  the taskbar button you clicked, on whichever monitor, at any DPI. It sizes itself to its content.
- **Desktop apps** are discovered from `shell:AppsFolder` (the Start menu's All apps list), which
  covers both classic installers and Store/MSIX apps. That runs on a background STA thread and
  the results are cached.
- **Native AOT friendly throughout**: `x:Bind` only (no reflection bindings), source-generated
  JSON, and source-generated COM interop (`LibraryImport`, `GeneratedComInterface`) for the shell,
  icon extraction and the jump list.

| Path | Contents |
| --- | --- |
| `Program.cs` | Entry point: forwards to the running instance, or becomes it |
| `App.xaml(.cs)` | The background instance; handles show / launch / quit requests |
| `FlyoutWindow.xaml(.cs)` | The flyout |
| `Models` | Config/state classes, JSON context, and the `AgentItem` view model |
| `Services` | Settings I/O, PATH lookup, launching, agent resolution, IPC, autostart, jump list |
| `Interop` | Win32 and shell COM declarations, icon extraction, Start menu enumeration |

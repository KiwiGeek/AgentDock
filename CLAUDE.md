# Agent Dock: handoff for coding sessions

Read this before changing anything. It records what exists, why it was built this way, and what
has and hasn't been verified. Keep it current: when a decision changes or something gets verified,
update this file in the same commit.

## What it is

A Windows 11 taskbar app. The user pins `AgentDock.exe`; clicking it opens a Fluent acrylic flyout
just above the taskbar button, listing AI agents:

- **CLI agents** (Claude Code, Codex, Gemini CLI, GitHub Copilot CLI, OpenCode by default) launch
  in Windows Terminal, in a working folder chosen from a recent-folders drop-down or a folder picker.
- **Desktop agents** (Claude, ChatGPT, Copilot, Perplexity by default) launch directly.

Agents that aren't installed are hidden. On startup, if the exe isn't pinned to the taskbar,
it asks the user to pin it and quits if they decline (see "Pin prompt" below). The list is user-editable in `agents.json`. Right-clicking
the taskbar icon shows the same agents as a jump list. The two goals that drive most decisions:
**looks native to Windows 11**, and **opens fast**.

Repo: `KiwiGeek/AgentDock` on GitHub. The `.csproj` is at the repo root (CI depends on that).

## Working with the owner

- Primary stack is C#. Uses ReSharper; has Telerik toolkits but they are deliberately not used
  here (built-in WinUI controls match the OS better and avoid a licence dependency).
- **Never credit Claude in git history.** No `Co-Authored-By` trailers, no "Generated with Claude"
  lines in commit messages or PR descriptions, no `claude/` or similar prefixes in branch names.
  Commits are authored by the owner's git identity only.
- Not confident with git release workflows. Any change to how releases work must be reflected,
  step by step, in `RELEASING.md`. When telling the owner to push or tag, give exact commands.

## Status

| Area | State |
| --- | --- |
| C# | Type-checks with zero warnings, including Native AOT and CsWinRT analyzers |
| XAML | Compiled on the owner's machine after one fix (a field name clashed with XAML-generated `_contentLoaded`). Confirm with a clean build |
| Runtime | Partly run (2026-10-15). See "Verify first" for what passed |
| CI | **Missing.** `.github/workflows/build.yml` and `RELEASING.md` are referenced here but were never committed; neither is in git history |

History: first built as WPF, then rewritten in WinUI 3 at the owner's request for a more native look.
Data files (`agents.json`, `state.json`, icon cache) are compatible across both versions.

## Verify first

Build and run, then check these in order. Results from the first run (2026-10-15, Debug build,
4-monitor setup) are marked ✅ passed or ⚠️ problem found; unmarked items are not yet tested.

1. ✅ `dotnet build` succeeds from a clean state (delete `bin/` and `obj/` first). Zero warnings.
2. ⚠️ `dotnet run` shows the flyout centred on screen, with acrylic, rounded corners, and no title bar.
   Centred, rounded and borderless all pass. Problem: the window never gets `WS_EX_TOPMOST`
   (exstyle is `0x100`) despite `IsAlwaysOnTop = true`, so without foreground it sits behind
   other windows.
3. ⚠️ The flyout **takes keyboard focus** when it appears: arrow keys move through agents, Esc hides it.
   Through the pipe hand-off (with foreground rights, as a taskbar click gives) it takes focus,
   and Down crosses from the CLI list into the desktop list. A direct first launch without
   foreground rights leaves it visible, unfocused and behind other windows. Esc not yet confirmed.
4. ✅ Clicking elsewhere hides it; running `dotnet run` again shows it again (single-instance hand-off).
   It also follows the cursor to whichever monitor it's on.
5. Height fits the content: no clipped footer, no large empty area. Status messages that wrap
   (e.g. a launch error) grow the window. The normal list fits exactly; wrapping not yet tested.
   The off-screen warm-up in `PrepareAsync` worked: the first show was fully rendered.
6. Published and pinned: the flyout appears above the clicked taskbar button, on the right monitor,
   at 100% and 150%+ scaling.
7. Clicking the pinned icon while the flyout is open closes it (doesn't close-and-reopen).
8. CLI agent opens Windows Terminal in the selected folder; desktop agent launches.
9. ✅ Desktop agents show their real icons (Start menu lookup and icon extraction work).
10. Jump list shows agents under "Command line" / "Desktop apps" plus "Quit Agent Dock", and entries launch.
11. Folder picker opens, and picking a folder doesn't hide the flyout.
12. `dotnet publish -c Release -r win-x64` succeeds with Native AOT.
13. The GitHub Actions run on push goes green.

Most likely to need work: 3 (focus), 5 (sizing via `Root.Measure` on a hidden window),
and `PrepareAsync`'s off-screen warm-up (content may not load from `AppWindow.Show(false)`;
there's a 2-second timeout fallback).

## Build, run, publish

```powershell
dotnet run                                   # Debug, JIT, centred on screen
dotnet run -- --quit                         # stop the background instance
$env:AGENTDOCK_PIN_CHECK = "1"; dotnet run   # Debug with the pin prompt (skipped in Debug otherwise)
dotnet publish -c Release -r win-x64 -o C:\Tools\AgentDock   # Native AOT
```

- **Quit the installed copy first** (`C:\Tools\AgentDock\AgentDock.exe --quit`). Only one instance
  runs per session; a Debug run otherwise hands off to the installed copy and exits silently.
- Native AOT needs Visual Studio's "Desktop development with C++" workload (MSVC linker).
- Debug builds don't write the sign-in Run key (`#if DEBUG` in `Autostart.cs`).
- Releases: push a `v*` tag; `.github/workflows/build.yml` builds and attaches the zip. See `RELEASING.md`.

## Architecture

### Process model (why it opens fast)

```
click pinned icon ──▶ AgentDock.exe #2 ──"Show\n" over named pipe──▶ AgentDock.exe #1 (resident)
                      (no WinUI loaded,                               flyout already built; un-hides
                       exits in milliseconds)                          and positions at cursor
```

- `Program.Main` deliberately touches no WinUI/WinRT types. It takes mutex `Local\AgentDock.Resident`.
  If another instance owns it, it calls `AllowSetForegroundWindow(ASFW_ANY)` (so the resident
  instance may take focus), sends one line over the pipe, and exits.
- Otherwise it becomes the resident instance: `RunResident` (marked `NoInlining` so WinUI assemblies
  load only on this path) starts the WinUI `Application`.
- Pipe name: `AgentDock.{UserName}.{SessionId}`, `PipeOptions.CurrentUserOnly`.
  Messages: `Show`, `Launch <agentId>`, `Background`, `Quit` (see `DockCommand` in `Services/Ipc.cs`).
  The pipe is unbuffered, so a write blocks until the resident reads it. `Ipc.TrySend` gives up
  after 3 s so that a hung resident can't hang the sender too (it did before; found in testing).
- Command-line: no args = Show; `--launch <id>` (jump list); `--background` (sign-in startup);
  `--quit`.
- `--quit` (`Program.QuitResident`) sends `Quit`, then waits up to 3 s for the resident's mutex.
  A hung resident still accepts the connection, so a successful send proves nothing. If the
  mutex isn't released in time, or the send fails, it kills every other `AgentDock` process in
  the session. Verified against a responsive copy (~0.2 s) and a suspended one (~3.2 s).
- `App.PrepareAsync` path: the flyout is built at startup, rendered once off-screen, then hidden.
  After that it is only ever shown and hidden, never recreated. `Alt+F4` and `AppWindow.Closing`
  are intercepted to hide; only `Quit` closes it (`App.IsExiting`).
- `startWithWindows` (default true) writes `HKCU\...\Run\AgentDock = "<exe>" --background`.

### Flyout behaviour (`FlyoutWindow.xaml.cs`)

- `AppWindow` with `OverlappedPresenter`: no title bar, not resizable, always on top,
  `IsShownInSwitchers = false` (no taskbar button, not in Alt+Tab). `DesktopAcrylicBackdrop`.
- **Placement** (`PlaceWindow`) works in physical pixels: cursor position at show time →
  monitor work area via `MonitorFromPoint`/`GetMonitorInfo` → DPI via `GetDpiForMonitor`.
  Taskbar edge is inferred from where the cursor sits outside the work area. If the cursor is
  inside the work area (launched from a terminal), the flyout centres. Height comes from
  `Root.Measure(380 DIP, ∞)`, capped at 640 DIP and the work area.
- **Hide on deactivation**, except while the folder picker is open (`_keepOpen`).
- **Toggle guard**: clicking the taskbar icon while open deactivates (hides) on mouse-down,
  then launches #2 on mouse-up. `ShowFlyout` ignores a show within 600 ms of a deactivation-hide.
- **Data reloads on every show** (settings and state files are tiny), so config edits apply
  without restarting. The slow pass (`RefreshFromShellAsync`: Start menu enumeration + icon
  extraction on a background STA thread) runs at most every 2 minutes or when icons are missing.
  A generation counter discards results from an earlier show.
- Number keys 1–9 launch the corresponding available agent. Up/Down cross between the two lists.
- Open animation: 250 ms slide + fade, skipped when Windows animations are off.

### Pin prompt (`PinPromptWindow.xaml(.cs)`, `Interop/TaskbarPins.cs`)

- The taskbar icon is the only way to open the flyout, so `App.StartAsync` calls
  `PinPromptWindow.EnsurePinnedAsync()` before building the flyout. Declining (the Quit button or
  closing the window) removes the Run key and quits, so it doesn't ask again at every sign-in.
- **Windows won't let an unpackaged app pin itself.** `TaskbarManager.RequestPinCurrentAppAsync`
  needs package identity plus a limited access feature granted by Microsoft, and the old "pin"
  shell verb is gone. So the prompt is an ordinary window *with* a taskbar button, and asks the
  user to right-click that button and choose Pin to taskbar. Windows then creates a shortcut to
  the exe with no arguments (verified).
- Detection: every desktop-app taskbar pin is a `.lnk` in
  `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar`. `TaskbarPins.IsPinned`
  resolves each with `IShellLinkW` + `IPersistFile` and compares it to `Environment.ProcessPath`.
  The prompt polls once a second and closes itself when the pin appears.
- Debug builds skip the check unless `AGENTDOCK_PIN_CHECK=1`, so `dotnet run` doesn't need its own pin.
- Verified: pinning closes the prompt and opens the flyout; Quit and closing the window both exit.

### Agents

- `AgentCatalog.Resolve` turns `AgentDefinition`s into `AgentItem`s.
  CLI: `PathResolver.Find` (PATH + PATHEXT, like cmd). Desktop, in priority order: explicit
  `path` → explicit `appId` → first match of `startMenuNames` in the Start menu's All apps list.
- Desktop app ids found via the Start menu are cached in `state.json` (`desktopApps`) so they
  show instantly on the next open, before the slow pass confirms them.
- `Launcher`: CLIs run **inside a shell** (`pwsh -NoLogo -NoExit -Command <cmd>`, or cmd `/k`) so npm
  `.cmd` shims work and the user lands at a prompt afterwards. Windows Terminal via `wt.exe -d <folder>`
  with `;` escaped as `\;` (wt treats bare `;` as a command separator); otherwise a console window.
  Desktop: exe via `UseShellExecute`, or `shell:AppsFolder\<appId>`.

### Shell interop

- Start menu enumeration: `SHCreateItemFromParsingName("shell:AppsFolder")` →
  `BindToHandler(BHID_EnumItems)` → `IEnumShellItems`. Name = `SIGDN_NORMALDISPLAY`,
  launch id = `SIGDN_PARENTRELATIVEPARSING`.
- Icon source, decided by `Services/AgentIcons.cs`, in order:
  1. The agent's `icon` setting (an image file, or an exe/dll).
  2. A compiled-in icon. `Assets/Agents/<name>.png` files are `EmbeddedResource`s named
     `AgentIcons.<name>.png`, with `Content Remove` so no loose copies ship. They match by agent id,
     or for CLI agents by command name. The source string is `bundled:<name>`.
  3. Desktop apps only: the Start menu (`shell:AppsFolder\<id>`) or the configured exe.
  4. Otherwise null, which shows the glyph.
  - CLI exes are deliberately **not** used: npm shims have none, scoop shims have none, and some
    real exes carry the wrong icon (OpenCode's is the Bun runtime's mascot). The owner asked for
    compiled-in icons instead.
- Shell icon extraction (desktop fallback only): `IShellItemImageFactory.GetImage` (64 px, icon only)
  → `GetDIBits` into a top-down 32-bit buffer (keeps alpha; legacy all-zero-alpha icons are made
  opaque) → PNG via WinRT `BitmapEncoder` into `%LOCALAPPDATA%\AgentDock\Icons\<sha1-prefix>.png`
  → `BitmapImage`. Image files and compiled-in icons skip the cache and load directly.
  Everything is held in memory for the life of the process.
- The jump list only takes `.exe`/`.dll`/`.ico`, so compiled-in PNGs are written out as
  `Icons\<name>.ico` (an ICO can wrap a PNG as-is). They're rewritten when the size no longer matches.
- Jump list: WinUI has no jump list API for unpackaged apps, so `Services/JumpList.cs` drives
  `ICustomDestinationList` directly. Entry text comes from the link's `PKEY_Title` property.
  `AppendCategory` can fail when the user has disabled recent items; that failure is ignored by design.

## Hard constraints (don't undo these without discussing)

**Native AOT compatibility.** `PublishAot` is on. Therefore:
- No reflection-based APIs. No `dynamic` (the WPF version used `dynamic` for Shell.Application;
  that was removed for this reason).
- JSON only via `DockJsonContext` (source-generated). New serialised types must be added there.
- COM via `[GeneratedComInterface]` + `[LibraryImport]` only, never `[ComImport]`/`DllImport`.
  In COM interface declarations, every vtable slot before the last one used must be declared, in order.
- XAML bindings via `x:Bind` only, never `{Binding}`.
- Classes crossing into WinRT (view models, `FlyoutWindow`, `PinPromptWindow`, `App`) are `partial`.

**Don't name members anything the XAML compiler generates.** It adds `_contentLoaded`,
`InitializeComponent`, `Connect`, `GetBindingConnector`, `Bindings`, and one field per `x:Name`
to `App`, `FlyoutWindow` and `PinPromptWindow`. A clash breaks the temporary compile, after which the XAML compiler
also reports misleading "Cannot resolve DataType" errors (WMC0909) and WMC1509 "No LocalAssembly".
Fix the clash first; those follow.

**`Program.Main` must stay WinUI-free.** Referencing a WinUI or WinRT type there loads it on the
click path and loses most of the startup gain.

**The resident process must not crash.** `App.UnhandledException` marks exceptions handled.
Shell, icon and jump list code catches broadly (`when ex is not OutOfMemoryException`); failures
there degrade to a missing icon or entry, never an error.

**Unpackaged, self-contained Windows App SDK.** No MSIX, nothing for the user to install.

## Files

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point: hand-off or become resident |
| `App.xaml(.cs)` | Resident instance; dispatches `DockCommand`s; jump-list launches |
| `FlyoutWindow.xaml(.cs)` | The flyout UI, placement, show/hide, keyboard |
| `PinPromptWindow.xaml(.cs)` | Startup prompt asking the user to pin Agent Dock; waits for the pin |
| `Models/Settings.cs` | `AgentDefinition`, `DockSettings` (+ defaults), `DockState`, `DockJsonContext` |
| `Models/AgentItem.cs` | View model for an agent row (`INotifyPropertyChanged`); `FolderItem` |
| `Services/SettingsStore.cs` | `%APPDATA%\AgentDock\agents.json` and `state.json`; atomic writes via temp file |
| `Services/AgentCatalog.cs` | Definition → resolved item |
| `Services/PathResolver.cs` | PATH/PATHEXT lookup |
| `Services/Launcher.cs` | Starts agents and plain shells |
| `Services/Ipc.cs` | `DockCommand`, named pipe client/server |
| `Services/Autostart.cs` | HKCU Run entry |
| `Services/JumpList.cs` | Taskbar jump list |
| `Interop/Win32.cs` | Win32 P/Invoke and structs |
| `Interop/ShellCom.cs` | Shell COM interfaces, GUIDs, constants |
| `Interop/StartMenuApps.cs` | All-apps enumeration; `RunOnStaThread` helper |
| `Interop/ShellIcons.cs` | Icon loading: compiled-in, image files, and shell extraction with a PNG cache |
| `Services/AgentIcons.cs` | Which icon an agent gets; compiled-in icon lookup; .ico files for the jump list |
| `Assets/Agents/*.png` | Built-in agent icons, compiled in as resources |
| `Interop/TaskbarPins.cs` | Whether an exe is pinned (reads the taskbar's pin shortcuts) |
| `.github/workflows/build.yml` | CI on push/PR; release on `v*` tag (win-x64 only) |
| `README.md`, `RELEASING.md` | User docs; step-by-step release process |

## Config and data

- `%APPDATA%\AgentDock\agents.json`: user-edited. Comments and trailing commas allowed.
  A malformed file is never overwritten: defaults are used for that session and the footer shows
  the error with a line number. Schema is documented in `README.md`; keep the two in sync.
- `%APPDATA%\AgentDock\state.json`: app-managed (current folder, recent folders, cached desktop app ids).
- `%LOCALAPPDATA%\AgentDock\Icons\`: PNG icon cache.

## Conventions

- Match the existing style: file-scoped namespaces, nullable enabled, XML doc comments that
  explain *why* rather than restating the code.
- UI text: sentence case, plain wording, no exclamation marks.
- Don't add NuGet packages without a clear reason; keep the dependency list to the Windows App SDK
  and SDK build tools.
- Bump `<Version>` in the `.csproj` only as part of a release (see `RELEASING.md`).
- When behaviour changes, update `README.md` and this file in the same commit.

## Planned work (agreed with the owner 2026-10-15)

Order: icons (done), then presets, then Claude usage. Each is its own commit.

### Done: agent icons

Built-in icons are compiled in (see "Shell interop"). Where each one came from (2026-10-15):

- **From installed apps:** `claude`, `copilot`, `claude-desktop` and `chatgpt-desktop`, exported
  at 256 px through the shell.
- **Downloaded from official sites, with the owner's OK:**
  - `gemini`: geminicli.com/icon.png
  - `opencode`: opencode.ai/web-app-manifest-512x512.png
  - `perplexity`: perplexity.ai/apple-touch-icon.png (180 px)
  - `copilot-desktop`: the copilot.com favicon, Microsoft's CDN. Its largest frame is 48 px, so it
    is slightly soft at 200% scaling.
  - `codex`: openai.com/apple-icon.png, the OpenAI mark (180 px). Codex has no logo of its own on
    any official page or in its repo, so it looks like ChatGPT's. Replace it if one appears.

### 2. Presets (an agent pinned to a folder, for work that repeats)

- Stored in `%APPDATA%\AgentDock\presets.json`. It's separate from `agents.json` because the app
  writes it, and rewriting `agents.json` would drop the user's comments. Hand edits are allowed.
  Schema: `{ "presets": [ { "name": "Agent Dock", "agent": "claude", "folder": "W:\\AgentDock", "arguments": "--continue" } ] }`.
  `agent` is an agent id. `arguments` is optional and replaces the agent's own arguments.
  Names are unique, which is enforced on save. CLI agents only: desktop apps don't open in a folder.
- Flyout: a "Presets" section above "Command line". Each row shows the agent's icon, the preset
  name, and "Claude Code · W:\AgentDock" underneath. Number keys count presets first.
  - A missing folder shows the row dimmed with "Folder not found".
  - A preset whose agent isn't installed follows `hideUnavailable`.
- Creating and editing:
  - Right-click a CLI agent (or press Shift+F10 / the menu key) and choose "Save as preset for
    <folder>". The name defaults to the folder name.
  - Right-click a preset for "Rename" and "Remove".
- Jump list: a "Presets" category first. New command `--preset "<name>"` → `DockCommand` `LaunchPreset`.
- New serialised types go in `DockJsonContext`. Update `README.md` (schema and usage).

### 3. Claude usage limits in the flyout (replaces the owner's dead WaifuOps status line)

- **The only official source** is Claude Code's status-line input (https://code.claude.com/docs/en/statusline):
  - `rate_limits.five_hour.used_percentage` and `.resets_at` (Unix seconds).
  - The same pair under `rate_limits.seven_day`.
  - Present only for claude.ai Pro/Max, and only after a session's first response. Either window can be missing.
- **Ruled out:** there's no CLI command, documented file or endpoint for these numbers. `/usage`
  doesn't work with `-p`. The Admin API is organisation-only. Reading `.credentials.json` to call
  an internal endpoint is against Anthropic's terms for third-party apps.
- **Bridge:** `AgentDock.exe --statusline`, handled in `Program.Main` before the mutex. It stays
  WinUI-free, and Native AOT keeps it fast, which matters because Claude Code runs it often.
  - It reads the stdin JSON and writes `rate_limits` plus the time to
    `%LOCALAPPDATA%\AgentDock\claude-usage.json` (atomic write).
  - It prints the status line `Opus 5.5 | 5h 10% | 7d 2%`.
  - If the user had a different status-line command before, it runs that with the same input
    and prints that output instead, so nothing else changes.
- **Setup is one-time and asks first:** a "Show Claude usage" prompt in the flyout.
  - It backs up `~/.claude/settings.json`, then sets `statusLine` to `"<exe>" --statusline`.
  - It edits with `JsonNode` so every other key is preserved.
  - If the exe moves, it detects the stale path and offers to fix it.
- **Display:** two thin bars under the Claude Code row (and under its presets).
  - "Session 10% · resets 3:00 pm" and "Week 2% · resets Thu".
  - Shows "Updated 12 min ago" when the data is old.
  - Once `resets_at` has passed, the bar drops to 0% and is labelled reset.
  - Hidden when there's no data.
- **Limitation, to state in the README:** the numbers update only while a Claude Code session is
  running, because the status line isn't run on a timer.

## Possible next steps (none requested yet; ask before starting)

- ARM64 build in CI (needs ARM64 MSVC tools on the runner for Native AOT cross-compilation).
- A settings UI instead of hand-editing `agents.json`.

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
  Messages: `Show`, `Launch <agentId>`, `LaunchPreset <name>`, `Background`, `Quit` (see `DockCommand`
  in `Services/Ipc.cs`; the name may contain spaces, since only the first space splits).
  The pipe is unbuffered, so a write blocks until the resident reads it. `Ipc.TrySend` gives up
  after 3 s so that a hung resident can't hang the sender too (it did before; found in testing).
- Command-line: no args = Show; `--launch <id>` and `--preset "<name>"` (jump list);
  `--background` (sign-in startup); `--quit`.
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
| `Services/ClaudeStatusLine.cs` | `--statusline` mode, saved Claude usage, and setting up Claude Code's status line |
| `Services/CodexUsage.cs` | Codex plan usage, read from Codex's own session logs |
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

Order: icons (done), then presets (done), then Claude usage (built). Each is its own commit.

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

### Built: presets (an agent pinned to a folder, for work that repeats)

- **Storage:** `%APPDATA%\AgentDock\presets.json` (`PresetList` / `Preset` in `Models/Settings.cs`).
  - It's separate from `agents.json` because the app writes it, and rewriting `agents.json` would
    drop the user's comments. Hand edits are allowed.
  - `agent` is an agent id. `arguments` is optional and replaces the agent's own arguments.
  - Names are unique and can't contain `"` (it would break the jump list argument). Both are
    enforced when saving and renaming.
  - If the file is malformed, the footer says so and the app refuses to save, so hand edits are
    never overwritten.
- **Rows:** a preset is an `AgentItem` built by `AgentCatalog.ResolvePresets` from the resolved
  agent, with `Preset` set.
  - `Name` and `Subtitle` come from the preset. `IsAvailable` also requires a CLI agent and an
    existing folder.
  - An unknown agent id gets a placeholder definition, so the row still shows and can be removed.
- **Flyout:** a "Presets" list above "Command line". Number keys count presets first; Up/Down
  cross all three lists.
  - A missing folder or an unknown agent shows the row dimmed, with the reason underneath.
  - A preset whose agent isn't installed follows `hideUnavailable`.
- **Editing:** `Agent_ContextRequested` (right-click, Shift+F10, the menu key) builds a `MenuFlyout`.
  - CLI agents get "Save as preset for <folder>". The name defaults to the folder name, with
    " 2", " 3" added if taken. Saving the same agent and folder twice is refused.
  - Presets get "Rename" (a `Flyout` with a text box: Enter saves, light dismiss cancels) and "Remove".
- **Jump list:** a "Presets" category first, available presets only, running `--preset "<name>"`.
  `App.LaunchPreset` shows the flyout with a message if the preset is unknown or can't launch.
- **Verified 2026-10-15:**
  - Rows render for a valid preset, a missing folder and an unknown agent, with renumbered keys.
  - A malformed file shows its error and hides presets.
  - The jump list has the Presets category with the right icon.
  - `--preset` with an unknown name, and with a missing folder, shows the message.
  - The owner confirmed right-click save, rename and remove by hand.
- **Not yet verified:** Shift+F10 / the menu key (a synthetic Shift+F10 opened nothing in one
  automated try, before `ContainerOf` replaced the `DataContext` lookup), and actually launching a
  preset into a terminal.

### Built: Claude usage limits (replaces the owner's dead WaifuOps status line)

All in `Services/ClaudeStatusLine.cs`, plus the drawer in `FlyoutWindow`.

- **The only official source** is Claude Code's status-line input (https://code.claude.com/docs/en/statusline):
  - `rate_limits.five_hour.used_percentage` and `.resets_at` (Unix seconds).
  - The same pair under `rate_limits.seven_day`.
  - Present only for claude.ai Pro/Max, and only after a session's first response. Either window
    can be missing, and Claude Code drops a window once it has reset.
- **Ruled out:** there's no CLI command, documented file or endpoint for these numbers. `/usage`
  doesn't work with `-p`. The Admin API is organisation-only. Reading `.credentials.json` to call
  an internal endpoint is against Anthropic's terms for third-party apps.
- **Bridge:** `AgentDock.exe --statusline`, the first thing `Program.Main` checks (before the
  mutex). It stays WinUI-free, and Native AOT keeps it fast, which matters because Claude Code runs it often.
  - It parses stdin with `JsonDocument` and saves `ClaudeUsage` to
    `%LOCALAPPDATA%\AgentDock\claude-usage.json`. The temp file name includes the PID, since
    sessions can refresh at the same moment.
  - A missing window keeps its previous value. Input with no `rate_limits` leaves the file alone.
  - It prints `Opus 5.5 | 5h 37% | 7d 12%`. Bad input still prints a line and exits 0.
  - **It doesn't chain to a previous status line.** The owner wanted it to replace WaifuOps.
- **Setup:** an `InfoBar` in the drawer, shown only when Claude Code is installed.
  - "Set up" names the status line it will replace. It backs up `settings.json` to
    `settings.json.agentdock-<timestamp>.bak`, then sets `statusLine` to `"<exe with forward
    slashes>" --statusline`.
  - It edits with `JsonNode`, so every other key is preserved, and writes with relaxed escaping
    (`UnsafeRelaxedJsonEscaping`), so quotes stay as `\"` rather than six-character unicode escapes.
  - Closing the offer sets `DockState.ClaudeUsageOfferDismissed`.
  - If the command runs a *different* AgentDock.exe, a warning offers "Update". It can't be
    dismissed, because usage would silently stop updating.
  - `CLAUDE_CONFIG_DIR` is honoured, the same as Claude Code. Testing used it to point at a
    scratch copy, so the owner's real settings were never touched.
- **Display:** in the drawer (see below), shown while the pointer is over Claude Code or its presets.
  - Session and Week bars, each with "37% · resets 16:08" (or "Mon 1:58", or a date).
  - "Updated N min ago" underneath, because Claude Code only refreshes the status line while a
    session is running.
  - Bars use `ShowPaused` from 75% and `ShowError` from 90% (Windows' warning and error colours).
  - A window whose `resets_at` has passed shows "0% · reset".
  - Before any data arrives, a note says it appears after the next reply.
  - Format gotcha: `"ddd t"` in a custom format means "day plus the first letter of AM/PM", so the
    time is formatted separately.
- **Verified 2026-10-15:**
  - Piped samples: full input, partial input, no `rate_limits`, garbage and empty input.
  - Setup against a scratch copy: only `statusLine` changes, other keys stay identical, and the
    backup matches the original.
  - The offer text, the usage display (then a column), and the "different copy" warning with Update.
  - The owner clicked Set up for real at 14:44: their status line now runs the bin\Debug exe, and
    the backup is `settings.json.agentdock-20261015-144457.bak`.
- **Verified:** real usage numbers from a Claude Code session reached the drawer (48% session,
  21% week, in the owner's screenshot).
- **Not yet verified:** the published Native AOT build.
- **Dev gotcha:** with the status line pointing at bin\Debug, every Claude Code refresh briefly
  runs that exe. A build can hit a short lock (MSBuild retries), and while the exe is mid-rebuild
  the status line may be blank for a moment.

### Drawer (owner's request: "only appear when Claude CLI is moused over")

- A windowed `Popup` (`ShouldConstrainToRootBounds=False`) with its own `DesktopAcrylicBackdrop`,
  beside the flyout. The flyout window itself stays 380 DIP wide.
- `AgentRow_PointerEntered` opens it when `PrepareDrawer(item)` returns true. That method also
  fills the drawer, and leaves it untouched for agents with nothing to show, so a closing drawer
  doesn't go blank.
  - Today: Claude Code (usage, or the setup or update offer) and Codex (usage), plus their presets,
    matched by command name (`CommandName`).
  - Moving between two drawer agents swaps the content in place.
  - Usage is loaded on the first hover after each show and cached until the next show
    (`ClearUsageCache` in `ReloadData`), so opening stays fast.
  - Add new agents in `PrepareDrawer`.
- Leaving the row or the drawer starts a 250 ms close timer, so the pointer can move into the
  drawer to click Set up.
- Placement: no gap (per the owner), on the right if it fits in the monitor's work area, otherwise
  on the left. The touching side has square corners and no border. Its height matches the flyout.
  - It tucks `DrawerOverlapDip` (8) under the flyout's edge. The flyout window keeps DWM's rounded
    corners, which left a notch at each end of the seam (owner's screenshot); the overlap covers them.
    That strip of the flyout is only padding.
  - Changing the flyout's DWM corner preference instead would have squared its outer corners too.
- **Opening animates `DrawerContent.Width`** from 1 to 248 DIP (240 plus the overlap; 250 ms
  ease-out), and closing goes back (200 ms ease-in), with `EnableDependentAnimation`. The popup
  window grows and shrinks with it, acrylic and all. On the left side, `DrawerContent_SizeChanged`
  keeps `HorizontalOffset = overlap - width`, so the drawer grows leftwards.
  - **Don't slide the content with a RenderTransform.** That was the first version: the windowed
    popup grows its HWND to cover the transformed bounds, so mid-slide the drawer's window sat
    *over* the flyout and clipped its list. The owner's screen recording showed it.
  - `DrawerPanel` (the content, fixed width) is anchored to the outer edge, so it slides out with it.
- **Shared hover image:** while the hovered agent has a drawer, `LayoutBackdrop` sizes the
  background image across the flyout and drawer together (UniformToFill, right-aligned, vertically
  centred). Each layer's flyout and drawer images get a negative margin for their slice, so the
  two halves line up (allowing for the overlap).
  - The drawer images are anchored to the drawer edge that touches the flyout, which doesn't move.
    So the picture stays lined up and is uncovered as the drawer grows.
  - Agents without a drawer keep the flyout-only layout.
  - Layout needs the image's pixel size, so it reruns on `ImageOpened` the first time.
- Hiding the flyout closes it at once.
- Switching an open drawer to another agent fades the new content in (`FadeInDrawerContent`,
  `DrawerPanel` opacity 0 to 1 over 300 ms).
- **Verified by the owner:** the drawer opens on hover without hiding the flyout, with acrylic and
  rounded corners (screenshot, 2026-10-15). A recording then confirmed no gap and the shared image,
  but showed the overlap while sliding. That was fixed by animating the width, and the owner
  confirmed it opens and closes well.

### Codex usage (`Services/CodexUsage.cs`)

- **Source:** Codex writes `token_count` events into its session logs,
  `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (`CODEX_HOME` moves the folder).
  - Each has `payload.rate_limits.primary` / `.secondary` with `used_percent`, `window_minutes`
    (300 and 10080 seen) and `resets_at` (Unix seconds). Older versions had `resets_in_seconds`,
    which is also handled. The line's `timestamp` is the "updated" time.
  - Seen with codex-cli 0.162.1. **Undocumented**, so every failure path returns null (no data).
    Never read `auth.json`.
- **Reading:** only the newest 3 day folders, newest log first, up to 10 logs. A session that got
  no reply has no `rate_limits`.
  - Only the last 512 KB of each log is read, scanning backwards for the last `"rate_limits"` line.
  - Windows are assigned by length (a day or less is the session window), not by primary/secondary.
- **Verified by the owner (2026-10-15):** the Codex picture, drawer, cross-fade between agents, and
  the seam overlap.

### Hover background (owner's request)

- Optional `background` per agent in `agents.json`: an image path, resolved like `icon`
  (`AgentIcons.ExistingFile`). Presets inherit it from their agent.
- **Two layers** (`BackdropLayer`): `HoverImageA`/`B` in `HoverBackdrop`, behind all content, and
  `DrawerImageA`/`B` in the drawer.
  - Moving to another agent puts its picture on the inactive layer and cross-fades: the new layer
    to 0.3 over 900 ms, the old to 0 over 600 ms (owner asked for "a fade between the two agents";
    timings tuned with the owner).
  - Moving back onto the agent whose picture is still fading out fades that same layer back in.
  - Leaving a row fades both layers out.
  - Each layer's sizing is done by `LayoutBackdrop(layer)` (see Drawer).
  Images are cached per path; the fade is skipped when Windows animations are off; hiding the
  flyout resets it.
- The image is a local setting, not compiled in or committed.

## Possible next steps (none requested yet; ask before starting)

- ARM64 build in CI (needs ARM64 MSVC tools on the runner for Native AOT cross-compilation).
- A settings UI instead of hand-editing `agents.json`.

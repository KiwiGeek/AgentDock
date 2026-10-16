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
- Placeholder defaults (Aider, Qwen Code, Cursor CLI, Amp, Kiro CLI, Goose, Crush, Factory Droid,
  Grok Build, Hermes Agent, DeepSeek Harness, Cursor desktop) were added 2026-10-16 and are under review: see `tasks.md`.
- There is no separate Codex desktop app: OpenAI merged it into the ChatGPT desktop app (July 2026).
  It's installed as `OpenAI.Codex_2p2nqsd0c76g0!App` but named "ChatGPT", so `chatgpt-desktop` finds it.
  A chatgpt.com site installed from Edge has the same Start menu name; `StartMenuApps.Load` prefers the
  real app over such web apps (`IsWebApp`).

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
- **Height can be dragged** (owner's request, 2026-10-16), on the list only. `HeightGrip` (a
  `ResizeGrip`, a `Grid` subclass because only a subclass can set `ProtectedCursor`) is a 6 DIP strip
  on the edge away from the taskbar (bottom if the taskbar is at the top); the edge by the taskbar
  stays put. The drag works in screen pixels (`GetCursorPos`) because the grip moves with the window.
  The result is saved as `DockState.FlyoutHeight` (client DIPs), which `PlaceWindow` uses instead
  of fitting the content (min 240 DIP, max the work area; the 640 DIP cap applies only to fitting).
  Double-clicking the grip clears it. The settings page has no grip and always fits its content.
  The window itself stays non-resizable. Not yet verified in-app.
- **Hide on deactivation**, except while the folder picker is open (`_keepOpen`).
- **Toggle guard**: clicking the taskbar icon while open deactivates (hides) on mouse-down,
  then launches #2 on mouse-up. `ShowFlyout` ignores a show within 600 ms of a deactivation-hide.
- **Data reloads on every show** (settings and state files are tiny), so config edits apply
  without restarting. The slow pass (`RefreshFromShellAsync`: Start menu enumeration + icon
  extraction on a background STA thread) runs at most every 2 minutes or when icons are missing.
  A generation counter discards results from an earlier show.
- Number keys 1–9 launch the corresponding available agent. Up/Down cross between the two lists.
  Neither does anything while the settings page is open.
- **Settings page:** the gear in the footer swaps the list for a page in the same window (so
  placement, acrylic and hide-on-deactivate stay as they are). The page resizes the flyout to its
  content. Two levels (owner's request, 2026-10-16):
  - **General**: Waifu backgrounds, Hide unavailable agents, Remember recents and **Clear recents**
    (a confirm flyout; disabled when nothing is saved). Remember recents is general on purpose,
    and its text doesn't name Claude: Claude is just the only agent recorded so far (owner,
    2026-10-16). Below it, **Agents**: a row for each installed agent that has a page of its own
    (`BuildAgentSettingsRows`), with its icon and a chevron.
  - **An agent's page** (`ShowAgentSettings`): the header shows the agent's name. Today only Claude
    Code has one (`ClaudeSettings`: "Usage limits", how Claude usage is found: "Read them from the status line" or "Ask Claude for them directly"). To add another, add a
    panel in the XAML, return it from `AgentSettingsPanelFor` (by command name), list it in
    `AgentSettingsPanels`, and fill it in `ShowSettingsPage`.
  - The back button and Esc go up one level (`SettingsBack`). Hiding the flyout returns to the
    list, and settings reopens at the first level.
  - Verified by screenshot (2026-10-16, driven through UI Automation): both levels render.
- Open animation: 250 ms slide + fade, skipped when Windows animations are off.
- **Banner** (owner's request, after the Windows 98 Start menu): a 32 DIP strip on the list's left
  edge, a gradient with "**Agent** Dock" reading bottom to top. The fill is 70% opaque
  (`BannerFillOpacity` must match the XAML); the text isn't, and is black or white, picked against
  the fill as it looks over the flyout. WinUI has no layout transform, so `LayoutBanner` rotates and
  places the text in a Canvas.
  - **Colour** (`SetBannerColors`): accent colours by default, and the highlighted agent's colour,
    fading over 450 ms. Order: the agent's `color` setting, then with waifus on its picture's colour
    (hand-picked for built-in pictures in `ThemeColors.BundledBackgrounds`, because automatic picking
    turned nearly all of them skin-tone orange), else worked out from its logo (`ThemeColors.FromStreamAsync`,
    good on logos; black and white logos get the accent). Leaving a row resets after 250 ms unless
    the pointer lands on another row or the drawer.
  - **Hearts** (waifus on only): a ❤️ above the text, turned sideways like it, beats twice every
    1.6 s, and a small heart floats up the strip from it on each beat (`FloatHeart`). They run only
    while the flyout is visible and Windows animations are on (`SyncHearts`).
  - `ListWidthDip` (412) is the banner plus the 380 DIP list; `ListColumn` and `FooterContent` match it.
    The footer's left padding lines its text up with the list, past the banner.
  - With the drawer on the left, the banner sits between the drawer and the list.
  - Verified by screenshot (2026-10-16): dark theme, translucent fill, sideways heart, white text.
    **Not yet verified:** the colour change on hover, the heart animation, light theme.

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
| `FlyoutWindow.xaml(.cs)` | The flyout UI, placement, show/hide, keyboard, settings page |
| `ResizeGrip.cs` | The strip that drags the flyout's height (shows the up-down cursor) |
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
| `Services/ClaudeUsageApi.cs` | "Ask Claude directly": the one usage GET, its token handling, rate limits and parser |
| `Services/ClaudeConfig.cs` | Claude Code's config folder (`CLAUDE_CONFIG_DIR` or `~\.claude`), shared |
| `Services/CodexUsage.cs` | Codex plan usage, read from Codex's own session logs |
| `Services/ThemeColors.cs` | The banner's colour per agent: hand-picked for built-in pictures, or worked out from an image |
| `Services/CopilotUsage.cs` | GitHub Copilot premium requests this month, via `gh api` (billing API) |
| `Assets/Agents/*.png` | Built-in agent icons (`<id>.png`) and hover backgrounds (`<id>-background.png`), compiled in as resources |
| `Interop/TaskbarPins.cs` | Whether an exe is pinned (reads the taskbar's pin shortcuts) |
| `.github/workflows/build.yml` | CI on push/PR; release on `v*` tag (win-x64 only) |
| `README.md`, `RELEASING.md` | User docs; step-by-step release process |
| `tasks.md` | Agreed work not yet started (placeholder agents, API-credit agents like DeepSeek) |

## Config and data

- `%APPDATA%\AgentDock\agents.json`: user-edited. Comments and trailing commas allowed.
  A malformed file is never overwritten: defaults are used for that session and the footer shows
  the error with a line number. Schema is documented in `README.md`; keep the two in sync.
  - The app never re-serialises it (that would drop comments). The one exception is the settings
    page's "Hide unavailable agents" switch: `SettingsStore.TrySetHideUnavailable` replaces just
    that value in the text (or adds it after the opening brace), checks the result reads back,
    then writes. It refuses if the file is malformed or the property appears more than once
    (e.g. also in a comment), and the switch reverts with a note. Not yet verified in-app.
- `%APPDATA%\AgentDock\state.json`: app-managed (current folder, recent folders, cached desktop app ids,
  whether waifu backgrounds are on, whether Claude's recent launches are remembered, and those
  folders, the dragged list height, and where Claude usage comes from).
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
- **Added 2026-10-16, at the owner's request** (scaled to at most 256 px):
  - `aider`: the Aider-AI GitHub organisation's avatar (same mark as aider.chat's 180 px icon).
  - `amp`: ampcode.com/app-icon-1024.png. `qwen`: the Qwen Code docs' favicon.png (512 px).
  - `kiro`: kiro.dev/apple-icon.png (192 px). `grok`: grok.com's android-chrome-512x512.png.
  - `goose`: the desktop app's icon in block/goose (`ui/desktop/src/images/icon-512.png`).
  - `crush`: charm.land's 180 px touch icon (Charm's ">c_" mark; Crush has no logo of its own).
  - `droid`: the Factory-AI GitHub organisation's avatar. `deepseek`: deepseek.com/favicon.ico (225 px).
  - `cursor-cli`: cursor.com's icon-512x512.png. Cursor desktop keeps the icon from the Start menu.
  - `hermes`: Hermes Agent's own icon from its site. Only 48 px, so it's soft at high scaling.

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

`Services/ClaudeStatusLine.cs` and `Services/ClaudeUsageApi.cs`, plus the drawer in `FlyoutWindow`.
Two sources, chosen on Claude Code's settings page (`DockState.ClaudeUsageSource`, `"statusLine"`
or `"api"` in state.json; missing means the status line). Both save to the same
`claude-usage.json`.

- **Status line (default).** The only documented source is Claude Code's status-line input
  (https://code.claude.com/docs/en/statusline):
  - `rate_limits.five_hour.used_percentage` and `.resets_at` (Unix seconds).
  - The same pair under `rate_limits.seven_day`.
  - Present only for claude.ai Pro/Max, and only after a session's first response. Either window
    can be missing, and Claude Code drops a window once it has reset.
  - No other documented source: no CLI command or file, `/usage` doesn't work with `-p`, and the
    Admin API is organisation-only.
- **Ask Claude directly** (owner's decision, 2026-10-16; `ClaudeUsageApi`). The same undocumented
  call Claude Code's `/usage` makes: one `GET {base}/api/oauth/usage` (`base` is
  `CLAUDE_CODE_CUSTOM_OAUTH_URL` or `https://api.anthropic.com`). Anthropic's terms describe
  subscription OAuth as for Claude Code and claude.ai and say third-party apps may not take those
  credentials; the owner chose to offer it anyway, off by default, with that stated in the UI and
  README. **Don't widen it** past this single GET of the user's own limits: no other endpoint, no
  POST, no profile call, no claude.ai endpoint, no browser or Claude Desktop cookies.
  - **Token:** `CLAUDE_CODE_OAUTH_TOKEN` if set (no expiry check), else `.credentials.json` in
    `ClaudeConfig.Directory` (shared with the status-line setup), read with `FileShare.ReadWrite`.
    Only `claudeAiOauth.accessToken` (or a root `accessToken`) and `expiresAt` (Unix ms) are read.
    An expired token sends nothing. **Never refresh it** (refresh tokens are single-use; refreshing
    would race Claude Code and sign the user out), never write the credentials file, never call the
    token endpoint, and never put the token in state.json, claude-usage.json, logs or the UI.
  - **Headers:** `Authorization: Bearer`, `anthropic-beta: oauth-2025-04-20`, and
    `User-Agent: claude-code/<version>`. The user agent puts the call in Claude Code's rate-limit
    bucket; any other agent gets a lasting 429. The version comes from one `claude --version` per
    process (5 s, no window), falling back to 2.1.201.
  - **Rate:** at most every 3 minutes after a success; after a 429, `Retry-After` (else 3 min),
    capped at 15 min. In memory only. The drawer caches its last result across shows and checks
    again only when that's over 3 minutes old, and only from `PrepareClaudeDrawer` (nothing runs
    while the flyout is hidden). Changing the setting drops the cache (`ClearUsageCache(sourceChanged)`),
    and an epoch counter ignores a check started before the change.
  - **Body:** `five_hour` / `seven_day` with `utilization` (already 0–100; `used_percentage` also
    accepted) and ISO-8601 `resets_at` (`ClaudeStatusLine.ResetsAt`, shared with the status line).
    A `limits` array (`kind`/`group` + `percent`) only fills a window the named object didn't give.
    Per-model weeks and `extra_usage` are ignored. A 200 with neither window is an empty success.
  - **Saving:** a success replaces the file (`ClaudeStatusLine.Replace`), keeping only the previous
    `Model`; it doesn't merge missing windows the way the status line does, because that answer is
    complete. A failure writes nothing.
  - **Drawer:** no Set up offer or other-copy warning. It shows the last result, else the saved
    file, straight away. On failure the saved numbers stay with a note under them ("… Showing the
    last saved limits."); with nothing saved, the note alone. Notes never include the token, a
    path, the response or an exception message. A status line set up earlier keeps writing the
    file, which is the fallback.
  - Verified 2026-10-16 with a throwaway console project (since deleted): every parser case in the
    spec; no credentials file → sign-in note; expired token → no request; against a local test
    server (not the live endpoint, with a dummy token): one GET with the right path and headers,
    the user agent read from the installed Claude Code (`claude-code/2.1.301`), and a 429 with
    `Retry-After` holding off the next request. **Not yet verified:** a real check against the
    owner's sign-in (the owner's to try).
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

- A column of the flyout window (`DrawerHost`), not a second window. The flyout grows by
  `DrawerWidthDip` (240) when it opens, so there is one frame, one acrylic and one toolbar.
  The list stays 380 DIP. `_listScreenX` is the list's left edge: opening right keeps it, opening
  left moves the window's left edge so the list doesn't slide.
- `AgentRow_PointerEntered` opens it when `PrepareDrawer(item)` returns true. That method also
  fills the drawer, and leaves it untouched for agents with nothing to show, so a closing drawer
  doesn't go blank.
  - Today: Claude Code (usage, or the setup or update offer), Codex (usage) and GitHub Copilot
    (premium requests), plus their presets, matched by command name (`CommandName`).
  - Moving between two drawer agents swaps the content in place.
  - Usage is loaded on the first hover after each show and cached until the next show
    (`ClearUsageCache` in `ReloadData`), so opening stays fast.
  - Add new agents in `PrepareDrawer`.
- Leaving the row or the drawer starts a 250 ms close timer, so the pointer can move into the
  drawer to click Set up or a recent folder. The whole column keeps it open, including the empty
  area under the usage (`Background="Transparent"`, or that area wouldn't receive the pointer).
- **Claude recents** (`Services/RecentLaunches.cs`): the last five folders Claude Code was launched
  in, under the usage. Clicking one launches Claude Code there. Launching a folder already in the
  list moves it to the front and shifts the ones above it down. At most five are stored, and the
  drawer shows only as many as fit under the usage without scrolling. Stored in `state.json`
  (`recentLaunches`, keyed by agent id) so `agents.json` isn't rewritten. The settings page calls
  the switch "Remember recents" (`rememberRecentLaunches`, default on; a missing property stays on).
  Off hides the list and stops recording; the saved folders stay for when it's turned back on.
  Presets of Claude share the agent's list. Only Claude is recorded so far.
  Each folder's context menu (right-click, Shift+F10, menu key) has "Remove from recents"
  (`RecentLaunches.Forget`). While that menu is open the drawer and banner colour stay
  (`_drawerMenuOpen`), since the popup takes the pointer out of the drawer. Removing the last folder
  closes the drawer if there's no usage to show. Not yet verified in-app.
- Placement: on the right if the grown window fits in the monitor's work area, otherwise on the
  left. Content sits against the list; the window growing uncovers it. The column is above the
  toolbar. The bar and its divider span the whole window; the buttons stay in the list's 380 DIP
  (`FooterContent`), so they don't travel with the window edge.
- Windows' own resize animation is disabled on this window (`DWMWA_TRANSITIONS_FORCEDISABLED`).
  Leaving it on made the list slide and then settle while the drawer grew.
- **Opening steps `DrawerHost.Width`** from 0 to 240 DIP (250 ms ease-out), and closing goes
  back (200 ms ease-in), from a dispatcher timer rather than a dependent storyboard. A storyboard
  changed the width during layout and the list slid sideways (owner's recording, 2026-10-15).
  The width callback resizes the window to match (`ApplyWindowForDrawer`).
  - It used to be a second popup. That couldn't share the frame or the toolbar. A RenderTransform
    on that popup also made its HWND cover the list (owner's screen recording).
- **Hover image:** one picture per layer, always sized to cover the list plus an open drawer (so
  it never zooms). Its position depends only on how far the drawer is open, not on which agent it
  belongs to (owner's request, 2026-10-16): drawer closed, the subject is centred in the list;
  fully open, the picture spans list and drawer (on the left side, its right edge stays on the
  list's right edge); in between it slides with the drawer's width animation, in the same tick as
  the window resize (`OnDrawerWidthChanged`). So moving from a drawer agent to one without, her
  picture starts in the drawer layout and slides to the centre as the drawer closes, with no empty
  grey drawer; the reverse slides the other way. Hovering a drawer agent from nothing also slides
  (it used to stay still while the window uncovered it). Layout needs the image's pixel size, so
  it reruns on `ImageOpened` the first time. Not yet verified in-app.
- Hiding the flyout closes it at once.
- Switching an open drawer to another agent fades the new content in (`FadeInDrawerContent`,
  `DrawerPanel` opacity 0 to 1 over 300 ms).
- **Verified by the owner (popup version):** the drawer opens on hover without hiding the flyout.
  The single-window version (the window itself grows) is not yet verified.

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

### GitHub Copilot usage (`Services/CopilotUsage.cs`)

- **No local source:** Copilot CLI (1.0.95) keeps no quota. `~/.copilot/logs` have nothing, and
  `data.db` only has `session_context_usage` (context window). `data.db`'s `accounts` table holds
  the access token: never read it.
- **Source:** GitHub's documented billing API,
  `GET users/{login}/settings/billing/premium_request/usage` (current month by default). The used
  count is the sum of `usageItems[].grossQuantity`.
  - It's run through `gh api` (no window, 15 s timeout, `GH_PROMPT_DISABLED`), so gh's own sign-in
    is used and Agent Dock never handles a token.
  - Needs gh's **`user` scope**. The owner runs `gh auth refresh -h github.com -s user` themselves:
    widening a token's access is their call. Until then the drawer shows that command.
  - 404 without a scope message means a plan provided by an organisation; the API only covers plans
    the user pays for themselves.
  - Don't pass a leading slash to `gh api` from Git Bash, which rewrites it into a file path.
- **Allowance:** the API has no allowance field, so `AgentDefinition.MonthlyAllowance` (agents.json
  `"monthlyAllowance"`) gives a bar ("42 of 300"). Without it the drawer shows a count only.
- **Display:** one row ("Premium requests"), resetting at 00:00 UTC on the 1st (`WeekGroup`
  hidden). Fetched in the background on hover and cached 5 minutes across shows; the drawer shows
  "Checking GitHub…" and fills in when the result arrives.
- **OPEN ISSUE (end of 2026-10-15): the owner reports it "did not work".** At that point
  `gh auth status` still listed only `gist, read:org, repo, workflow`, with no `user`, so the refresh
  apparently hadn't gone through, and the drawer would show the permission message.
  - Next session, check in order: did `gh auth refresh -h github.com -s user` finish (browser device
    flow)? Then `gh api users/KiwiGeek/settings/billing/premium_request/usage`.
  - Then check what the drawer actually shows. Is the message clear? Does the background fetch
    complete? It's unverified in-app.
  - If the API is still 404 with the scope, the plan may be provided by an organisation, or the
    endpoint may need `X-GitHub-Api-Version`.
  - The owner is on Copilot Pro: `"monthlyAllowance": 300` is set in their agents.json.

### Hover background (owner's request)

- **Compiled in** (owner's decision, 2026-10-15): `Assets/Agents/<agent id>-background.png` are
  `EmbeddedResource`s named `AgentBackgrounds.<id>-background.png`. The csproj excludes
  `*-background.png` from the icon glob, so they never count as icons.
  - Source string `bundled-background:<id>`, matched by agent id or CLI command name
    (`AgentIcons.FindBackground`). Loaded with `BitmapImage.SetSourceAsync` from the resource stream.
  - About 19 MB of PNGs, which is why the DLL is about 20 MB.
  - Two files were renamed to match ids: `github-copilot-background` became `copilot-background`,
    and `grok-backgorund` became `grok-background`.
- An optional `background` per agent in `agents.json` overrides the built-in one: an image path,
  resolved like `icon` (`AgentIcons.ExistingFile`). Presets inherit it from their agent.
- **Turned off from the settings page** (gear in the footer): "Waifu backgrounds" writes
  `DockState.ShowHoverBackgrounds` (`state.json`, default true). A missing property stays on, so
  older state files keep the pictures. Off skips the fade; the usage drawer still opens.
- **Two layers** (`BackdropLayer`): `HoverImageA`/`B` in `HoverBackdrop`, behind all content, and
  one image per layer across the whole window (see Drawer).
  - Moving to another agent puts its picture on the inactive layer and cross-fades: the new layer
    to 0.3 over 900 ms, the old to 0 over 600 ms (owner asked for "a fade between the two agents";
    timings tuned with the owner).
  - Moving back onto the agent whose picture is still fading out fades that same layer back in.
  - Leaving a row fades both layers out. Moving into the open drawer does not: the picture stays
    until the drawer closes.
  - Each layer's sizing is done by `LayoutBackdrop(layer)` (see Drawer). With the drawer closed,
    the picture is shifted so its subject is centred in the list. The built-in pictures put
    the character about 72% of the way across (`BackdropSubjectX`); new pictures should keep that layout.
  Images are cached per path; the fade is skipped when Windows animations are off; hiding the
  flyout resets it.
- **Not yet verified in-app:** loading from the embedded resources. The build embeds all 10, but
  nobody has hovered since.

## Possible next steps (none requested yet; ask before starting)

- ARM64 build in CI (needs ARM64 MSVC tools on the runner for Native AOT cross-compilation).
- More of the settings page. It exists (gear in the footer): general settings plus per-agent pages
  (only Claude Code has one so far). Pages for Codex or Copilot (e.g. Copilot's monthly allowance),
  and editing the agent list from the UI, are still not built.

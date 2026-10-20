# Agent Dock: handoff for coding agents

Read this before changing anything. The first sections are the catch-up: the owner's rules, lessons
learned, and where each agent stands. **Reference** below is the detail: architecture, every
decision, and what has and hasn't been verified. `tasks.md` is agreed work not yet started. Keep
both current: when a decision changes or something gets verified, update them in the same commit.

Agent Dock is a Windows 11 taskbar flyout (WinUI 3, unpackaged, Native AOT, C#) that launches AI
agents: CLI agents in Windows Terminal in a chosen folder, desktop agents directly. On hover, a
drawer beside the list shows the agent's plan usage and recent folders; with nothing hovered it
shows a usage overview of every agent with a suggestion of which to use.

## The owner's rules

**Git and releases**

- **Never credit an AI in git history.** No `Co-Authored-By` trailers, no "Generated with …" lines
  in commits or PR descriptions, no `claude/` (or similar) branch prefixes. Commits use the owner's
  git identity only. This overrides any tool's default attribution.
- Commit and push only when the owner asks. Don't commit `AgentDock.slnx` (the owner's untracked
  Visual Studio file).
- The owner isn't confident with release workflows: any change to how releases work goes, step by
  step, into `RELEASING.md`, and when asking them to push or tag, give the exact commands.
- Bump `<Version>` in the `.csproj` only as part of a release.

**Working on the code**

- **After every build, stop any running Agent Dock and relaunch the new build**, so the owner sees
  the change at once:
  ```powershell
  $exe = "W:\AgentDock\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AgentDock.exe"
  Get-Process AgentDock -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --quit }
  Get-Process AgentDock -ErrorAction SilentlyContinue | Stop-Process -Force
  dotnet build W:\AgentDock\AgentDock.csproj -nologo -v q
  Start-Process $exe -ArgumentList '--background'
  ```
- **Build the `.csproj`, not the folder.** A bare `dotnet build` picks up `AgentDock.slnx`, whose
  default platform is ARM64, and fails with NETSDK1032. `-p:Platform=x64` "works" but builds into
  `bin\x64\Debug`, which isn't the copy Claude Code's status line runs.
- Primary stack is C#, with ReSharper. Built-in WinUI controls only (no Telerik, no new NuGet
  packages without a clear reason).
- Hard constraints (details under Reference → "Hard constraints"): Native AOT (no reflection, no `dynamic`, JSON only via
  the source-generated `DockJsonContext`, COM via `[GeneratedComInterface]`/`[LibraryImport]`,
  `x:Bind` only); `Program.Main` stays WinUI-free; the resident process must never crash; don't name
  members anything the XAML compiler generates (`_contentLoaded`, `Bindings`, …).
- Style: file-scoped namespaces, nullable on, XML doc comments that say *why*. UI text: sentence
  case, plain words, no exclamation marks.
- When behaviour changes, update `README.md` and this file in the same commit.
- Downloaded icons come from the agent's official site or brand kit, recorded under Reference
  ("Done: agent icons"), scaled to at most 256 px.

**Product decisions**

- **The user chooses their agents.** A fresh install has an empty list; Find agents offers the
  known ones (`Services/KnownAgents.cs`). Known agents that weren't found can still be added
  ("Not found on this PC"), and are looked for again on every open (PATH is re-read from the
  registry each show).
- **Usage may read a CLI's own sign-in token** (owner's decision, 2026-10-16) when that's the only
  practical route: Cursor CLI's and Grok Build's `auth.json`. Rules for any token: read only; never
  refresh it (refresh tokens are single-use and the CLI would be signed out); never write the file;
  never log, show or save the token; an expired token sends nothing. Prefer a route where the agent
  or another CLI does the sign-in (Codex's app server, `gh api`), and keep using those where they exist.
- **Claude's direct usage check** is one GET of the user's own limits, off by default, with the
  terms caveat stated in the UI. Don't widen it (no other endpoint, no refresh, no cookies).
- Drawer and overview behaviour the owner corrected: the usage overview shows only while **no agent**
  is pointed at; hovering an agent without a drawer (e.g. Claude or ChatGPT desktop) closes the
  drawer and shrinks the window.
- Reordering is by dragging only (the Move up/down buttons were removed on request).
- Gemini CLI was removed (Google stopped serving personal accounts, June 2026); Antigravity CLI
  (`agy`) replaced it.

## Things learned the hard way

- The flyout window doesn't get `WS_EX_TOPMOST`, so without foreground rights it sits behind other
  windows. Screen captures of it then show whatever is on top; `PrintWindow` on a 200% monitor came
  out scaled wrong. **Check the UI through UI Automation instead** (rows and bars have automation
  names, e.g. "Open Codex: 1% session · resets 16:08 · 0% week · resets Mon 1:58").
- Opening the flyout from a script: run the exe with no arguments (it hands off to the resident
  copy over the pipe). A second run within 600 ms of a hide counts as a toggle.
- `gh api` from Git Bash: no leading slash (MSYS rewrites it into a file path).
- PowerShell: `foreach` can't sit inside `( … )`; use `$( … )` or assign it.
- Hand-set `Cookie` headers need `SocketsHttpHandler { UseCookies = false }`.
- A failed C# compile makes the XAML compiler report misleading WMC0909/WMC1509 errors; fix the C#
  error first.
- **A pinned taskbar icon can't be refreshed live** (tested 2026-10-19). Changing the icon of the
  pin's shortcut (`User Pinned\TaskBar\AgentDock.lnk`) showed on the taskbar only 2 times in about
  12. None of these made it reliable: `SHChangeNotify` (`UPDATEITEM` with or without `FLUSH`,
  `UPDATEDIR`, `ASSOCCHANGED`), `SHUpdateImage`, `ie4uinit -show`, fresh icon file names, waiting,
  or opening the flyout. Cendio's ThinLinc hit the same wall and gave up (bugzilla.cendio.com bug
  7970). **Restarting Explorer does work**, and the resident Agent Dock survives it. Check the
  taskbar through UI Automation (the button is named "AgentDock pinned"), capturing just its rectangle.
- Verify network parsers against the real service with a throwaway console project in the
  scratchpad that compiles the service file(s) directly, then delete it. Print numbers, never tokens.

## Agents: where each one stands

"Done" means everything that applies is built and verified against the real service: found and
launched, built-in icon and hover picture, usage in the drawer and the overview, recent folders.
Details per agent are under Reference; what's still to review per agent is in `tasks.md`.

### Command line

- [x] **Claude Code** (`claude`): icon, picture, usage (status line, or asked directly), recents,
  overview. Usage per account (`CLAUDE_CONFIG_DIR`).
- [x] **Codex** (`codex`): icon, picture, live usage from Codex's app server, recents, overview.
  Usage per account (`CODEX_HOME`).
- [x] **Grok Build** (`grok`): icon, picture, usage from grok.com with Grok Build's token, recents,
  overview. Its token lasts hours, so the drawer often shows saved numbers between Grok runs.
- [x] **Cursor CLI** (`cursor-agent`, or plain `agent`): icon, picture, usage from cursor.com with
  the CLI's token, recents, overview.
- [x] **GitHub Copilot CLI** (`copilot`): icon, picture, usage (AI credits) via
  `gh api copilot_internal/user`, recents, overview.
- [ ] **Antigravity CLI** (`agy`): known agent, icon and picture. Not installed on the owner's PC; no
  usage, not verified to launch.
- [ ] **OpenCode** (`opencode`): icon, picture and recent folders. No usage bars: its providers have
  separate billing and quota systems.
- [ ] **Aider** (`aider`): icon, picture, model setting, Aider-driven model search and recent folders. No usage bars: it can use arbitrary providers.
- [ ] **Qwen Code** (`qwen`): icon, picture and recent folders. No usage bars yet.
- [ ] **Amp** (`amp`): icon and picture only.
- [ ] **Kiro CLI** (`kiro-cli`): icon and picture only.
- [ ] **Goose** (`goose`): icon and picture only.
- [ ] **Crush** (`crush`): icon and picture only.
- [ ] **Factory Droid** (`droid`): icon and picture only.
- [ ] **Hermes Agent** (`hermes`): icon and picture only.
- [ ] **DeepSeek Harness** (`dsh web`): icon, picture, DeepSeek API key (`DEEPSEEK_API_KEY`),
  recent folders, hosted in the background (no Terminal window; Stop on the row). Subtitle
  "Opens in the browser". Launch folder becomes a Harness workspace via
  `tools/dsh-plugins` (`--patch` + `AGENTDOCK_WORKSPACE` + browser
  `?agentdock_workspace=`). No usage bars of its own (linked key shows the balance). Still to
  verify: launch, Stop, and the folder opening in the UI.

### Desktop

- [x] **Cursor** (Start menu "Cursor"): icon, picture, usage (shares Cursor CLI's account), and it
  **opens the working folder as a workspace** (`Cursor.exe <folder>`, added 2026-10-16), with presets
  and recent folders. The folder launch isn't yet verified by an actual launch.
- [x] **Claude** (desktop): icon, picture, and it **opens a Claude Code session in the working
  folder** (`claude://code/new?folder=`, added 2026-10-16; the owner confirmed it works), with
  presets and recent folders. Usage is Claude Code's default account (shared drawer content, one
  overview row). A row button and its right-click menu also open a new chat or Cowork session, with no folder.
- [ ] **ChatGPT** (desktop, the Codex app): icon, picture, and it **opens a new chat in the working
  folder** (`codex://threads/new?path=`, added 2026-10-16), with presets and recent folders. Usage is
  Codex's default account (assumes the app is signed in as the Codex CLI is). Not yet verified by an
  actual launch. A row button and its right-click menu also open the app plainly, without a folder.
- [ ] **GitHub Copilot** (desktop, `github-copilot-desktop`, Start menu "GitHub Copilot", GitHub's
  own app): icon, and it **opens a session in the working folder** through Copilot CLI's `copilot app`
  (added 2026-10-19), with presets, recent folders, and Copilot CLI's usage (same GitHub account:
  shared drawer content, one overview row). Its chat button opens the app without a folder. Picture:
  the goggles one that was Copilot CLI's until 2026-10-19 (the CLI has a new one with a terminal
  prompt). Verified: added through Add agents, the launch from its row (a session with `cwd` in the
  folder), recents; not yet: the hover drawer and picture by eye, the chat button.
- [ ] **Microsoft Copilot** (desktop, `microsoft-copilot`, Start menu "Copilot"; Store id
  `9NHT9RB2F4HD`): icon, picture, launch. No usage. Renamed 2026-10-19 from `copilot-desktop` /
  "Copilot" (owner: fix the legacy naming), which read as GitHub's next to GitHub Copilot.
  `KnownAgents.MigrateRenamed` moves older agents.json entries (catalog id, and the name if it was
  still "Copilot"; the agent's own id stays, since presets, recents and the jump list use it) and
  state.json's dismissed offers and taskbar icon choice. Not installed on the owner's PC.
- [ ] **Perplexity** (desktop): icon, picture, launch. No usage.

## Not done, across the app

- **CI and releases:** `.github/workflows/build.yml` and `RELEASING.md` are referenced but were
  never committed.
- **Not yet verified by eye:** the usage overview's look, hovering between agents, clicking an
  overview row; the new Cursor, Grok and Copilot drawers; drag to reorder; the published Native AOT
  build. See "Verify first" under Reference.

## Reference

The detailed handoff: what exists, why it was built this way, and what has and hasn't been verified.

### What it is

A Windows 11 taskbar app. The user pins `AgentDock.exe`; clicking it opens a Fluent acrylic flyout
just above the taskbar button, listing AI agents:

- **CLI agents** launch in Windows Terminal, in a working folder chosen from a recent-folders
  drop-down or a folder picker. **Hosted CLIs** (DeepSeek Harness: `HostedAgents`) run with no
  Terminal window; the flyout shows Stop while they're running, and quitting Agent Dock stops them.
  DeepSeek gets the launch folder as a Harness workspace through `tools/dsh-plugins` (`--patch`,
  `AGENTDOCK_WORKSPACE`, and a browser URL with `?agentdock_workspace=`).
- The General setting **Close terminal when agent exits** uses a non-persistent shell, closing Agent
  Dock's tab when the CLI returns. With Waifu backgrounds on, Agent Dock supplies each CLI agent's
  picture through hidden, app-owned Windows Terminal profiles in
  `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\AgentDock` (Store Terminal reads that folder
  too; a settings.json `"import"` key is not a real Terminal setting). It prepares profiles before
  launch, touches settings.json so Terminal reloads, and waits briefly; until then it opens an
  ordinary tab rather than risking a failed launch. Turning Waifus off removes the fragment folder
  and any `source: AgentDock` stubs Terminal copied into settings.json.
- **Desktop agents** launch directly.
- **The user chooses the agents** (owner's decision, 2026-10-16). A fresh install has an empty list
  and a welcome offering **Find agents**, which scans for the agents in `Services/KnownAgents.cs`
  (Claude Code, Codex, Antigravity CLI, Copilot CLI, OpenCode, Aider, Qwen Code, Cursor CLI, Amp, Kiro,
  Goose, Crush, Factory Droid, Grok Build, Hermes Agent, DeepSeek Harness; Claude, ChatGPT,
  GitHub Copilot, Microsoft Copilot, Perplexity and Cursor desktop) and adds the ones ticked. See "Agent list" below.
- Gemini CLI stopped serving personal Google accounts on 2026-06-18 (API keys and Code Assist
  licences still work), so it was removed from the known agents, with its icon and picture (owner,
  2026-10-16). Google moved those users to **Antigravity CLI** (`agy`, id `antigravity`), added the
  same day. It has a built-in icon and picture (added 2026-10-19; banner colour: her periwinkle top).
- There is no separate Codex desktop app: OpenAI merged it into the ChatGPT desktop app (July 2026).
  It's installed as `OpenAI.Codex_2p2nqsd0c76g0!App` but named "ChatGPT", so `chatgpt-desktop` finds it.
  A chatgpt.com site installed from Edge has the same Start menu name; `StartMenuApps.Load` prefers the
  real app over such web apps (`IsWebApp`).

On startup, if the exe isn't pinned to the taskbar,
it asks the user to pin it and quits if they decline (see "Pin prompt" below). The list lives in `agents.json`, managed from the settings page. Right-clicking
the taskbar icon shows the same agents as a jump list. The two goals that drive most decisions:
**looks native to Windows 11**, and **opens fast**.

Repo: `KiwiGeek/AgentDock` on GitHub. The `.csproj` is at the repo root (CI depends on that).

### Status

| Area | State |
| --- | --- |
| C# | Type-checks with zero warnings, including Native AOT and CsWinRT analyzers |
| XAML | Compiled on the owner's machine after one fix (a field name clashed with XAML-generated `_contentLoaded`). Confirm with a clean build |
| Runtime | Partly run (2026-10-15). See "Verify first" for what passed |
| CI | **Missing.** `.github/workflows/build.yml` and `RELEASING.md` are referenced here but were never committed; neither is in git history |

History: first built as WPF, then rewritten in WinUI 3 at the owner's request for a more native look.
Data files (`agents.json`, `state.json`, icon cache) are compatible across both versions.

### Verify first

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

### Build, run, publish

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

### Architecture

#### Process model (why it opens fast)

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
  Messages: `Show`, `Launch <agentId>`, `LaunchOther <mode> <agentId>`, `LaunchPreset <name>`, `Background`, `Quit` (see `DockCommand`
  in `Services/Ipc.cs`; the name may contain spaces, since only the first space splits).
  The pipe is unbuffered, so a write blocks until the resident reads it. `Ipc.TrySend` gives up
  after 3 s so that a hung resident can't hang the sender too (it did before; found in testing).
- Command-line: no args = Show; `--launch <id>` (with `--mode <mode>` for Claude's or ChatGPT's
  other ways to open) and `--preset "<name>"` (jump list);
  `--background` (sign-in startup); `--quit`.
- `--quit` (`Program.QuitResident`) sends `Quit`, then waits up to 3 s for the resident's mutex.
  A hung resident still accepts the connection, so a successful send proves nothing. If the
  mutex isn't released in time, or the send fails, it kills every other `AgentDock` process in
  the session. Verified against a responsive copy (~0.2 s) and a suspended one (~3.2 s).
- `App.PrepareAsync` path: the flyout is built at startup, rendered once off-screen, then hidden.
  After that it is only ever shown and hidden, never recreated. `Alt+F4` and `AppWindow.Closing`
  are intercepted to hide; only `Quit` closes it (`App.IsExiting`).
- `startWithWindows` (default true) writes `HKCU\...\Run\AgentDock = "<exe>" --background`.

#### Flyout behaviour (`FlyoutWindow.xaml.cs`)

- `AppWindow` with `OverlappedPresenter`: no title bar, not resizable, always on top,
  `IsShownInSwitchers = false` (no taskbar button, not in Alt+Tab). `DesktopAcrylicBackdrop`.
- **Placement** (`PlaceWindow`) works in physical pixels: cursor position at show time →
  monitor work area via `MonitorFromPoint`/`GetMonitorInfo` → DPI via `GetDpiForMonitor`.
  Taskbar edge is inferred from where the cursor sits outside the work area. If the cursor is
  inside the work area (launched from a terminal), the flyout centres. Height comes from
  `Root.Measure(380 DIP, ∞)`, capped at 640 DIP and the work area.
- **Height can be dragged** (owner's request, 2026-10-16), on the list. `HeightGrip` (a
  `ResizeGrip`, a `Grid` subclass because only a subclass can set `ProtectedCursor`) is a 6 DIP strip
  on the edge away from the taskbar (bottom if the taskbar is at the top); the edge by the taskbar
  stays put. The drag works in screen pixels (`GetCursorPos`) because the grip moves with the window.
  The result is saved as `DockState.FlyoutHeight` (client DIPs), which `PlaceWindow` uses instead
  of fitting the content (min 240 DIP, max the work area; the 640 DIP cap applies only to fitting).
  Double-clicking the grip clears it. Settings shared this height until it became its own window
  (2026-10-19), which is resizable by itself. The flyout stays non-resizable. Not yet verified in-app.
- **Hide on deactivation**, except while the folder picker is open (`_keepOpen`).
- **Toggle guard**: clicking the taskbar icon while open deactivates (hides) on mouse-down,
  then launches #2 on mouse-up. `ShowFlyout` ignores a show within 600 ms of a deactivation-hide.
- **Data reloads on every show** (settings and state files are tiny), so config edits apply
  without restarting. Each show also re-reads PATH from the registry (`PathResolver.RefreshPath`:
  machine then user, with this process's extra entries kept at the end), because the resident
  instance starts at sign-in and would otherwise never see a CLI installed later; shells it starts
  get that PATH too. The slow pass (`RefreshFromShellAsync`: Start menu enumeration + icon
  extraction on a background STA thread) runs at most every 2 minutes, or on every show while icons
  are missing or a desktop agent in the list isn't found (owner's request, 2026-10-16: agents added
  but not detected are looked for again each time). Not yet verified in-app.
  A generation counter discards results from an earlier show.
- Keys 1–9, then A–Z once the digits run out (owner's request, 2026-10-19), launch the corresponding
  available agent; not with Ctrl, Alt or Windows held, and letters not while the folder box has the
  focus (it uses them to find a folder). Up/Down cross between the two lists.
  Esc only hides the flyout, since settings isn't in it any more.
- **Settings window** (`SettingsWindow.xaml(.cs)`, `SettingsWindow.ApiAccounts.cs`; owner's decision,
  2026-10-19: master-detail after a stacked page and a section-only nav both felt wrong). The flyout
  no longer hosts settings. The gear (`OpenSettings` → General), the context menu's Settings
  (`agentId`), the welcome's Find agents and the offer's Choose (`addAgents: true`), and the
  overview's unlinked API row (`section: "api"`) go to `App.OpenSettings`, which **hides the flyout
  first** and shows the window.
  - **The window:** Mica (`BaseAlt`, the stronger tint), with the content extended into a tall title
    bar (`ExtendsContentIntoTitleBar`, `AppTitleBar` with icon and title, transparent caption buttons,
    title dimmed while inactive; owner's request, 2026-10-20). The left list has no fill, so it sits
    on the Mica; the page sits on `LayerFillColorDefaultBrush` with a rounded top-left corner, as in
    Windows' Settings. Verified by a screen capture 2026-10-20. A normal title bar's behaviour stays: a taskbar button and Alt+Tab entry, resizable (minimum
    560×400 DIP), 880×720 DIP at first (wider since 2026-10-20, for the Accounts rows), then wherever the user leaves it. One instance owned by `App`;
    closing only hides it; `Quit` closes it. Esc hides the window (ignored while a pop-up is open).
  - **Master list** (hand-built `ListView`s, rebuilt by `RebuildNavMenu`): **General** and
    **Accounts**; an **Agents** header, then each agent (icon + name) and **Add agents**. Selecting an
    item shows only that item's details on the right, filling the pane up to 1000 DIP and left-aligned (a star column with a MaxWidth; owner's choice, 2026-10-20, after Windows' own Settings). Add agents' custom-command form stays at most 560 DIP. Until 2026-10-20 API
    keys had their own section in this list; the owner found settings scattered and keys hard to share
    between agents, so they moved into Accounts (design: the "Agent Dock settings redesign" canvas).
  - **General**, in groups (`SectionHeaderStyle`): **Flyout** (Usage overview, Hide unavailable
    agents, Waifu backgrounds), **Taskbar icon** (see "Taskbar icon"), **Launching** (Terminal and
    Shell, agents.json's `terminal` / `shell`, which had no UI before; Close terminal when agent exits;
    Start with Windows, which also calls `Autostart.Apply` at once), **Recent folders** (Remember
    recents and **Clear recents**). Remember recents is general on purpose (several agents record
    them, including OpenCode and Aider). agents.json settings are disabled while it can't be read.
    Turning **Waifu backgrounds** on asks a second time (`SettingsWindow.Waifu.cs`, owner's request,
    2026-10-20): a `ContentDialog` with one of 62 jokes picked at random (`WaifuPrompts`: title,
    line, a yes like "Just do it, I'm not proud", a no like "I have some dignity left"; no is the
    default button). Backing out puts the switch back; turning them off never asks. Verified
    2026-10-20 through UI Automation: the prompt opened, backing out left state.json and Terminal's
    fragments untouched.
  - **Accounts** (`ShowAccounts`, `SettingsWindow.ApiAccounts.cs`): **Plans**, the user's agents
    grouped by the sign-in their usage comes from (`PlanGroups`: Claude and Codex per `UsageAccount`,
    one each for Cursor, GitHub and Grok; agents on another provider or on an Anthropic key are left
    out), each row opening its first agent's page, where the usage settings are. No bars there (the
    flyout has them). **API keys**, one row per account (service, the key shortened, who uses it, the
    balance line), **Add a key**, and **Suggest plans before API keys** (`PreferPlansOverApi`, moved
    here from General). The flyout's unlinked-key row (`section: "api"`) opens this page.
  - **An API key's page** (`ShowApiAccount`, Accounts › name in the title, `AccountsCrumb`): Key (name,
    service, key, Anthropic's Admin key), **Agents that use this key** (`FillApiAgentRows`: every
    agent in the list that can take that service's key, plus any linked to it that can't, each with a
    switch that sets its `apiAccount`; one account per agent, so switching on moves it from another
    key, and the row says so; Claude Code's row says it leaves its plan), Balance (services with one),
    and Remove account.
  - **An agent's page** (`ShowAgentSettings`): see "Agent list" below. **Account** comes first, right
    under the name (`UpdateAgentApiSection`: radio buttons, None plus the keys it can take, for agents
    that take keys or are linked), then Aider's model, arguments, and **Environment variables** in an
    `Expander`, open only when it has some. Duplicate, Remove. Drag agents
    in the left list to reorder (`AgentNavList`, same as the flyout). With waifus on, her hover
    picture sits behind the page (`SyncAgentBackdrop`, same opacity as the flyout). Agent-specific
    panels: Claude's usage source, Grok / Cursor / Copilot notes, Aider model + search
    (`AgentSettingsPanelFor` / `UpdateAgentPanel`).
  - **Add agents** (`ShowAddAgents`): the scan and a custom command; see "Agent list". Adding from
    the welcome or the offer hides the window when done.
  - **Data:** loads agents.json and state.json on every `Show`; own `ShellIcons`. Changes save at once;
    `SettingsChange` → `FlyoutWindow.NotifySettingsChanged`. State via `UpdateState` so the two windows
    don't overwrite each other's keys. **Known gap:** a flyout-side agents.json edit while the window
    is open isn't seen until it's shown again.
  - Verified 2026-10-19: master-detail build compiles. Verified 2026-10-20 through UI Automation:
    General's groups and Launching controls, Accounts with the owner's five plans and DeepSeek key
    (with its real balance), Add a key opening the key's page with OpenCode and Aider listed, Remove
    account, Aider's Account choice and note. **Not yet verified by eye:** the look of the Accounts
    rows and the key page, switching an agent on or off there, drag to reorder, deep links, an
    agent actually starting with a key it gets from an account (other than Aider).
- Open animation: 250 ms slide + fade, skipped when Windows animations are off.
- **Banner** (owner's request, after the Windows 98 Start menu): a 32 DIP strip on the list's left
  edge, a gradient with "**Agent**Dock" (no space; bold, then semi-light) reading bottom to top. The fill is 70% opaque
  (`BannerFillOpacity` must match the XAML); the text isn't, and is black or white, picked against
  the fill as it looks over the flyout. WinUI has no layout transform, so `LayoutBanner` rotates and
  places the text in a Canvas.
  - **Colour** (`SetBannerColors`): accent colours by default, and the highlighted agent's colour,
    fading over 450 ms. Order: the agent's `color` setting, then its picture's colour even when
    waifus are hidden (hand-picked for built-in pictures in `ThemeColors.BundledBackgrounds`, because
    automatic picking turned nearly all of them skin-tone orange), else worked out from its logo
    (`ThemeColors.FromStreamAsync`, good on logos; black and white logos get the accent). Leaving a row resets after 250 ms unless
    the pointer lands on another row or the drawer.
  - **Hearts** (waifus on only): a ❤️ above the text, turned sideways like it, beats twice every
    1.6 s. Small hearts float from it to the top of the strip (`FloatHeart`; owner's request,
    2026-10-19: irregular, the whole height, several at once): released every 0.4–2.6 s, now and then
    two or three together (`ReleaseHearts`), each with its own size, tilt, rocking, wobble, brightness
    and speed (55–110 DIP/s, so several are on the way up at once; at most 8). Opening the flyout
    starts with three to five already partway up, spread over the trip (`SeedHearts`, through
    `Storyboard.Seek`; owner's request, 2026-10-19). What floats is picked by weight from
    `Floaters`: mostly hearts (❤️ 💕 💖 💗 💞 🩷, sideways), now and then 💋 🌸 ✨ (nearly upright).
    No faces: too busy at that size. They run only
    while the flyout is visible and Windows animations are on (`SyncHearts`). Not yet seen by eye.
  - `ListWidthDip` (412) is the banner plus the 380 DIP list; `ListColumn` and `FooterContent` match it.
    The footer's left padding lines its text up with the list, past the banner.
  - With the drawer on the left, the banner sits between the drawer and the list.
  - Verified by screenshot (2026-10-16): dark theme, translucent fill, sideways heart, white text.
    **Not yet verified:** the colour change on hover, the heart animation, light theme.

#### Pin prompt (`PinPromptWindow.xaml(.cs)`, `Interop/TaskbarPins.cs`)

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

#### Agents

- `AgentCatalog.Resolve` turns `AgentDefinition`s into `AgentItem`s.
  CLI: `PathResolver.Find` (PATH + PATHEXT, like cmd). Desktop, in priority order: explicit
  `path` → explicit `appId` → first match of `startMenuNames` in the Start menu's All apps list.
- Desktop app ids found via the Start menu are cached in `state.json` (`desktopApps`) so they
  show instantly on the next open, before the slow pass confirms them.
- `Launcher`: CLIs run **inside a shell** (`pwsh -NoLogo -NoExit -Command <cmd>`, or cmd `/k`) so npm
  `.cmd` shims work and the user lands at a prompt afterwards. Windows Terminal via `wt.exe -d <folder>`
  with `;` escaped as `\;` (wt treats bare `;` as a command separator); otherwise a console window.
  Desktop: exe via `UseShellExecute`, or `shell:AppsFolder\<appId>`.
  - **Desktop apps that open in a folder** (owner's request, 2026-10-16): `KnownAgents.FolderLink`
    gives a link with `{folder}` (URL-encoded with `Uri.EscapeDataString`), matched by catalog id.
    **ChatGPT** (the Codex app, package `OpenAI.Codex`, registers `codex`): `codex://threads/new?path=<path>`,
    documented in OpenAI's Commands reference (learn.chatgpt.com/docs/reference/commands; also
    `prompt=` and `originUrl=`; `codex://new?…` needs at least one parameter). It replaced
    `codex app <path>`, which didn't open the folder on Windows (openai/codex #26423, fixed by
    deep link). Not yet tried by an actual launch.
    **Claude**: `claude://code/new?folder=<path>`, documented by Anthropic
    (support.claude.com, "Open Claude Desktop with a link"; also `q`/`prompt` to prefill, `file`
    accepted but unsupported). Claude asks to confirm the folder every time, even a trusted one.
    Known Windows bug (anthropics/claude-code #92260): with the app already open, the folder is
    added to the open workspace instead of a new window. The app is packaged
    (`Claude_pzs8sxrjxfjjc`) and its manifest registers the `claude` protocol.
  - **Without a folder** (owner's request, 2026-10-19): Claude and ChatGPT are general assistants
    too, so `KnownAgents.OtherLaunches` lists other ways to open them, by catalog id. Claude:
    "New chat" (`claude://claude.ai/new`) and "New Cowork session" (`claude://cowork/new`), both
    on the same support.claude.com page (which also documents `q` to prefill, unused). ChatGPT:
    "Open without a folder", the plain `shell:AppsFolder` launch (owner's choice). The app registers
    only `codex`, and OpenAI's Commands reference has no link for a regular ChatGPT chat;
    `codex://threads/new` without `path` would still be a Codex chat. The first one is a button
    on the row (`AgentItem.QuickLaunch`, agent rows only, not presets; a Button inside the list
    item, so it doesn't fire `ItemClick`). All of them are in the row's right-click menu, and in
    the jump list as "Claude: new chat" etc. (`--launch <id> --mode <mode>`, `DockCommandKind.LaunchOther`,
    payload "mode id"). `Launcher.Launch(item, OtherLaunch)` doesn't record a recent folder.
    Verified 2026-10-19 through UI Automation: the "New chat" and "Open without a folder" buttons
    are on the rows. **Not yet verified:** an actual launch from the button, the menu or the jump list.
  - **GitHub Copilot desktop** (owner's request, 2026-10-19; installed per user as
    `%LOCALAPPDATA%\Programs\GitHub Copilot\github.exe`, registers `ghapp`, `github-app` and `gh`) has
    no documented link for a local folder (docs.github.com, "Using deep links to open the GitHub
    Copilot app": session links take `OWNER/REPO`). Copilot CLI's `copilot app` does it: it creates a
    session workspace for the current folder in `~\.copilot\session-state` and opens
    `github-app://sessions/<id>` (read from the CLI's bundle, 1.0.95). So `Launcher` runs
    `copilot app` in the folder with no window (`KnownAgents.OpensFolderThroughCli`); without Copilot
    CLI on PATH it opens the app plainly. Making the session ourselves would copy Copilot's internals.
    Its chat button opens the app with no folder, like ChatGPT's (owner, 2026-10-19; it briefly opened
    Microsoft Copilot instead, with that app's picture on hover, which was taken out again). Usage is Copilot CLI's (`IsGitHubCopilot`, by catalog id or the `copilot` command). Without Copilot
    CLI the folder isn't recorded as a recent (`KnownAgents.CanOpenFolder`), and its settings page
    says why (`CopilotSettings`, shown for Copilot CLI too: where the AI credits come from).
  - **Cursor** has no such link, but is a VS Code fork, so it opens a folder given on its command
    line (`KnownAgents.OpensFolderInExe`; its documented `cursor .` shell command does the same).
    `FolderAppExe` finds Cursor.exe: the agent's `path`, else three folders up from the `cursor`
    shim (`resources\app\bin\cursor.cmd`), else `%LOCALAPPDATA%\Programs\cursor\Cursor.exe`. The shim
    isn't run (a console script; it would flash a window). Already open on that folder, Cursor
    switches to that window. It opens a workspace, not the agent panel (no documented way to).
  - `AgentItem.TakesFolder` (CLI, or `KnownAgents.TakesFolder`: a folder link or an exe that takes the folder) is what presets, "Save as preset", recents
    (`RecentLaunches.Records`), the jump list's "Open X in folder" text and the drawer's recents use.
    Claude desktop's drawer is Claude Code's (`PrepareClaudeDrawer` with its account, see "Usage per
    account"); another such desktop agent's drawer would show only its recents. `App.LaunchPreset`
    now resolves desktop apps (cached app ids, then the Start menu). Its row's subtitle stays
    "Desktop app" (owner: the folder note was unnecessary).
  - Verified by the owner (2026-10-16): launching Claude desktop opens a Code session in the folder.
  - **`env`** (CLI only) is set *inside* the shell command, in front of the agent:
    `${env:NAME} = "…"; claude` for PowerShell, `set "NAME=…" && claude` for cmd. Not on the
    process: when Windows Terminal is already running, wt.exe hands the tab to that Terminal, which
    starts it with its own environment. For PowerShell with variables, the script is passed with
    `-EncodedCommand`, so Windows Terminal cannot split its semicolons. `%NAME%` in a value stays a reference
    (`${env:NAME}` in PowerShell; cmd expands `%NAME%` itself), so a value can stay in Windows'
    environment instead of agents.json. Writing a key into agents.json directly is fine too
    (owner, 2026-10-19: their own keys aren't hidden from them).

#### Agent list (owner's decision, 2026-10-16)

- **Known agents** (`KnownAgents`, compiled in) are templates: id, name, command or Start menu names.
  Icons, pictures and banner colours are matched by these ids. A new agent ships by being added there.
  Nothing appears in the flyout until the user adds it.
- **The user's list** is `DockSettings.Agents` in agents.json, now **app-owned**: written by
  `SettingsStore.SaveSettings` (comments are lost; it's still hand-editable JSON). Added agents keep
  `catalog` (the known id), so a renamed or duplicated copy keeps its icon, picture and colour
  (`AgentIcons.Find`/`FindBackground` try id, then catalog, then the CLI command name).
  `SaveAgentList` refuses while agents.json failed to load (`_settingsError`), so a broken hand edit
  is never written over; settings then says why and hides Add agents.
- **First run:** an empty list and `DockState.AgentSetupDone` false shows the welcome
  (`UpdateSetupPanel`): **Find agents** opens Add agents and returns to the list after adding;
  **Not now** sets `AgentSetupDone`, after which an empty list shows "No agents yet" with Find agents.
  An older agents.json with agents in it simply is the list.
- **Add agents** (settings → Agents → "Scan for new agents", or "Add your own command", which scrolls to the form; "Scan again" on the page): `FindNewAgentsAsync` resolves known agents not already in the list
  (`KnownAgents.IsInList`: same catalog id, CLI command or Start menu name) against PATH and the Start
  menu (STA thread), shown ticked. Unticked ones go into `DismissedAgentOffers`. Known agents that
  weren't found are listed unticked under a collapsed **Not found on this PC (N)** expander
  (`MissingAgentsExpander`; opened by itself when nothing was found), for agents installed where
  Agent Dock doesn't look or not installed yet (owner's request, 2026-10-16). The same Add selected
  adds them; they show as "Not installed" until found. They aren't dismissed when left unticked, so
  installing one later still brings the offer. Also a custom
  command form (name, command, arguments). Not yet verified in-app.
- **Offers:** the slow pass (`RefreshFromShellAsync`) also resolves known agents not in the list;
  installed ones not dismissed are offered in an InfoBar at the top of the list ("Amp is installed.
  Add it?" with Add, or "N more agents are installed" with Choose). Closing it dismisses them. Never
  added automatically.
- **An agent's page** (`ShowAgentSettings`, settings → Agents → row): name, arguments (CLI), command
  shown, **environment variables** (CLI; add with name + value, remove with ×; names are letters,
  digits and underscores), **Duplicate** (a copy right after it, id from
  `KnownAgents.UniqueId`, same catalog, name "… (copy)", opened with its name selected), **Remove**
  (confirms; presets for it stay). Changes save immediately. The flyout's right-click menu on an agent also has **Settings** (opens this page) and **Remove from list** (`ConfirmRemoveAgent`, a confirm flyout beside the row). Agent-specific panels (Claude's usage
  source) show under it.
- **Reordering by dragging** (owner's request): the settings window's left agent list
  (`AgentNavList`, `AgentListRow`) and the flyout's own lists (presets, command line, desktop) all
  have `CanReorderItems`. A drop saves the order (`AgentNav_DragItemsCompleted`,
  `AgentList_DragItemsCompleted`); in the flyout, agents not shown in that list keep their places
  (`ReorderShown`). Presets save to presets.json, refused while it can't be read. Dragging is the
  only way to reorder (no Move up/down). Not yet verified by an actual drag.
- **Usage per account** (`Services/UsageAccount.cs`): the account is the config folder from the
  agent's `env` (`CLAUDE_CONFIG_DIR` / `CODEX_HOME`, `%NAME%` expanded, full path), else the default.
  `UsageAccount.Key` keys every cache, so copies on one account share numbers and accounts never mix.
  - **Claude desktop** (catalog `claude-desktop`) is Claude Code's default account, and **ChatGPT
    desktop** (`chatgpt-desktop`) Codex's (owner's request, 2026-10-16; it assumes the app and the
    CLI are signed in as the same user): `UsageAccount.For` returns it, ignoring `env` (it doesn't
    reach a desktop app). So their drawers, overview rows (shared with the CLI's), live checks, and
    for Claude the status line offer and usage-source choice all work as for the CLI. Code that
    picks the agent by command name asks `UsageAccount.For(…)?.Agent` first. Codex's check still
    needs the `codex` CLI on PATH.
  - Saved files: the default account keeps `claude-usage.json` / `codex-usage.json`; another uses
    `claude-usage-<sha1 of folder, 12 hex>.json` (`UsageAccount.UsageFile`).
  - **Codex:** `codex app-server` is started with the agent's env, so Codex answers as that account
    with its own sign-in. A signed-out folder gives the sign-in note, never the default's numbers.
  - **Claude, asked directly:** the token comes from the agent's `CLAUDE_CODE_OAUTH_TOKEN`, else
    that folder's `.credentials.json`; rate limiting (`Pace`) is per account.
  - **Claude, status line:** no extra argument is needed. Claude Code passes its environment to the
    status line command, so `--statusline` reads `CLAUDE_CONFIG_DIR` and writes that account's file.
    Set up installs into that account's `settings.json`; dismissing it is per account
    (`ClaudeUsageOfferDismissedFor`). The usage source is per account too: `ClaudeUsageSource` for the
    default, `ClaudeUsageSources[folder]` for others, set on the copy's own page (`UpdateAgentPanel`,
    rerun when its variables change).
  - An agent using an API key or another endpoint (`UsesOtherProvider`: base URLs, API keys,
    Bedrock/Vertex) has no plan: its drawer says plan limits don't apply, and its page hides the
    Claude usage choice.
  - Verified 2026-10-16: a status line run with another `CLAUDE_CONFIG_DIR` wrote its own file and
    left `claude-usage.json` untouched; account detection cases and a Codex check against an empty
    `CODEX_HOME` (sign-in note), in a throwaway console project (deleted).
- Verified 2026-10-16 by screenshots (UI Automation): the welcome, and Add agents finding the
  owner's installed agents with icons. Not yet verified: adding, the agent page, duplicate,
  environment variables reaching an agent, the offer.

#### Shell interop

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

### Hard constraints (don't undo these without discussing)

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

### Files

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point: hand-off or become resident |
| `App.xaml(.cs)` | Resident instance; dispatches `DockCommand`s; jump-list launches |
| `FlyoutWindow.xaml(.cs)` | The flyout UI, placement, show/hide, keyboard (settings are not here; see `SettingsWindow`) |
| `SettingsWindow.xaml(.cs)` | The settings window: master-detail (General, Accounts, each agent with drag reorder), Add agents, taskbar icon; saves at once and notifies the flyout |
| `SettingsWindow.ApiAccounts.cs` | The settings window's Accounts page, each API key's page, the agent page's Account choice, and its own balance checks |
| `Models/SettingsChange.cs` | Flags saying what a settings change touched, so the flyout refreshes only that |
| `ResizeGrip.cs` | The strip that drags the flyout's height (shows the up-down cursor) |
| `PinPromptWindow.xaml(.cs)` | Startup prompt asking the user to pin Agent Dock; waits for the pin |
| `Models/Settings.cs` | `AgentDefinition`, `DockSettings` (+ defaults), `DockState`, `DockJsonContext` |
| `Models/AgentItem.cs` | View model for an agent row (`INotifyPropertyChanged`); `FolderItem` |
| `Services/SettingsStore.cs` | `%APPDATA%\AgentDock\agents.json` and `state.json`; atomic writes via temp file |
| `Services/AgentCatalog.cs` | Definition → resolved item |
| `Services/KnownAgents.cs` | The agents Agent Dock can find and offer (templates; ids match icons and pictures) |
| `Services/UsageAccount.cs` | Whose plan an agent's usage belongs to (its config folder); per-account usage files; `UsageFetch` |
| `Models/AgentListRow.cs` | A row in the settings page's draggable agent list |
| `Services/PathResolver.cs` | PATH/PATHEXT lookup |
| `Services/Launcher.cs` | Starts agents and plain shells |
| `Services/HostedAgents.cs` | CLI agents run with no Terminal window (DeepSeek Harness); Stop from the row; killed on quit; `--patch` + browser open for launch-folder workspace |
| `tools/dsh-plugins/` | DeepSeek Harness plugins shipped with the build (`agentdock-workspace`: host create + client open) |
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
| `Services/CodexUsage.cs` | Codex plan usage, asked live of Codex's app server; last answer saved |
| `Services/ThemeColors.cs` | The banner's colour per agent: hand-picked for built-in pictures, or worked out from an image |
| `Services/GrokUsage.cs` | Grok Build usage from grok.com (gRPC-web, with Grok Build's own sign-in from `~\.grok\auth.json`) |
| `Services/CursorUsage.cs` | Cursor usage from cursor.com (`/api/usage-summary`, with Cursor CLI's sign-in from `%APPDATA%\cursor\auth.json`) |
| `Services/CopilotUsage.cs` | GitHub Copilot usage this month (AI credits against the plan), via `gh api copilot_internal/user` |
| `Assets/Agents/*.png` | Built-in agent icons (`<id>.png`) and hover backgrounds (`<id>-background.png`), compiled in as resources |
| `Interop/TaskbarPins.cs` | Whether an exe is pinned (reads the taskbar's pin shortcuts), and setting a pin's icon |
| `Services/ApiUsage.cs` | API keys' balances: the DeepSeek check, the 100% logic, which agents seem to use a provider |
| `FlyoutWindow.ApiAccounts.cs` | API usage in the flyout: the checks and the drawer (the settings section is in `SettingsWindow.ApiAccounts.cs`) |
| `Services/TaskbarIcon.cs` | The taskbar icon choices, applying one to the pin, restarting File Explorer to show it |
| `Models/TaskbarIconChoice.cs` | An icon in the settings page's taskbar icon picker |
| `Assets/AppIcons/` | Taskbar icon sources (`appicon-<id>.png`, 1024 px, not shipped) and the built `ico/<id>.ico` (compiled in) |
| `tools/Build-AppIcons.ps1` | Builds `Assets/AppIcons/ico/*.ico` from the sources (crop, sizes, sharpen) |
| `.github/workflows/build.yml` | CI on push/PR; release on `v*` tag (win-x64 only) |
| `README.md`, `RELEASING.md` | User docs; step-by-step release process |
| `AGENTS.md` | This handoff: the owner's rules, lessons learned, which agents are done, and the reference |
| `tasks.md` | Agreed work not yet started (agents to review, API-credit profiles, custom groups) |

### Config and data

- `%APPDATA%\AgentDock\agents.json`: the agent list and global settings, **written by the app**
  (settings page) and still hand-editable. Comments and trailing commas are read, but comments are
  lost on the next save. A malformed file is never overwritten: an empty list is used for that
  session, the footer shows the error with a line number, and nothing that would save it is
  allowed. Schema is documented in `README.md`; keep the two in sync.
- `%APPDATA%\AgentDock\state.json`: app-managed (current folder, recent folders, cached desktop app ids,
  whether waifu backgrounds are on, whether Claude's recent launches are remembered, and those
  folders, the dragged list height, where Claude usage comes from, the taskbar icon choice, and each
  API account's last balance and 100% amount).
- `%LOCALAPPDATA%\AgentDock\Icons\`: PNG icon cache.
- `%LOCALAPPDATA%\AgentDock\TaskbarIcons\`: the chosen taskbar icon, which the pin's shortcut points at.

### Conventions

- Match the existing style: file-scoped namespaces, nullable enabled, XML doc comments that
  explain *why* rather than restating the code.
- UI text: sentence case, plain wording, no exclamation marks.
- Don't add NuGet packages without a clear reason; keep the dependency list to the Windows App SDK
  and SDK build tools.
- Bump `<Version>` in the `.csproj` only as part of a release (see `RELEASING.md`).
- When behaviour changes, update `README.md` and this file in the same commit.

### Planned work (agreed with the owner 2026-10-15)

Order: icons (done), then presets (done), then Claude usage (built). Each is its own commit.

#### Done: agent icons

Built-in icons are compiled in (see "Shell interop"). Where each one came from (2026-10-15):

- **From installed apps:** `claude`, `copilot`, `claude-desktop` and `chatgpt-desktop`, exported
  at 256 px through the shell.
- **Downloaded from official sites, with the owner's OK:**
  - `opencode`: opencode.ai/web-app-manifest-512x512.png
  - `perplexity`: perplexity.ai/apple-touch-icon.png (180 px)
  - `microsoft-copilot` (was `copilot-desktop`): the copilot.com favicon, Microsoft's CDN. Its largest frame is 48 px, so it
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
  - `cursor` and `cursor-cli`, from Cursor's brand kit (cursor.com/brand, `cursor-brand-assets.zip`),
    scaled to 256 px (owner's request, 2026-10-16): the desktop app gets the app tile
    (`APP_ICON_25D_DARK`, the same design `cursor-cli` used to have), and the CLI the bare shaded cube
    (`CUBE_25D`, transparent, so it shows on both themes). Cursor has no logo of its own for the CLI.
  - `hermes`: Hermes Agent's own icon from its site. Only 48 px, so it's soft at high scaling.
  - `github-copilot-desktop` (2026-10-19): the installed app's own `icons\icon.png` (512 px), scaled to 256 px.
  - `antigravity`: antigravity.google/assets/image/antigravity-logo.png (276 px, transparent), the
    site's largest icon.

#### Built: presets (an agent pinned to a folder, for work that repeats)

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
  - `Name` and `Subtitle` come from the preset. `IsAvailable` also requires an agent that opens in
    a folder (`TakesFolder`: a CLI agent, or Claude Desktop) and an existing folder.
  - An unknown agent id gets a placeholder definition, so the row still shows and can be removed.
- **Flyout:** a "Presets" list above "Command line". Number keys count presets first; Up/Down
  cross all three lists.
  - A missing folder or an unknown agent shows the row dimmed, with the reason underneath.
  - A preset whose agent isn't installed follows `hideUnavailable`.
- **Editing:** `Agent_ContextRequested` (right-click, Shift+F10, the menu key) builds a `MenuFlyout`.
  - Agents that open in a folder get "Save as preset for <folder>". The name defaults to the folder name, with
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

#### Built: Claude usage limits (replaces the owner's dead WaifuOps status line)

`Services/ClaudeStatusLine.cs` and `Services/ClaudeUsageApi.cs`, plus the drawer in `FlyoutWindow`.
Two sources, chosen on Claude Code's settings page (`DockState.ClaudeUsageSource`, `"statusLine"`
or `"api"` in state.json; missing means the status line). Both save to the same file, per account:
`claude-usage.json` for the default, `claude-usage-<hash>.json` for another (see "Usage per account" under Agent list).

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
    Per-model weeks (`seven_day_<model>`, or `weekly_<model>` in `limits`, but never `weekly_all`,
    which is the all-models week: a real reply had it, and it showed as "All week") become `PlanUsage.ModelWeeks`
    with a label ("Sonnet"), shown as extra bars; `extra_usage` (`is_enabled`, `used_credits`,
    `monthly_limit`, unit undocumented, so shown as plain numbers) becomes `PlanUsage.Extra` (owner's
    request, 2026-10-16). The status line doesn't send these, so its saves keep the last direct
    check's. A 200 with neither window is an empty success.
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
  - Session and Week bars, each with "37% · resets 16:08" (or "Mon 1:58", or a date). Then a bar per
    model week ("Sonnet week") and an "Extra usage: 12.5 of 50 used" line when known
    (`ShowUsageExtras`, shared with Codex, which shows "Extra usage credits: …"). Under the title, the plan
    (Codex: "Team plan") or the last model ("Opus 5.5, last used", from the status line).
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

#### Drawer (owner's request: "only appear when Claude CLI is moused over")

- A column of the flyout window (`DrawerHost`), not a second window. The flyout grows by
  `DrawerWidthDip` (240) when it opens, so there is one frame, one acrylic and one toolbar.
  The list stays 380 DIP. `_listScreenX` is the list's left edge: opening right keeps it, opening
  left moves the window's left edge so the list doesn't slide.
- `AgentRow_PointerEntered` opens it when `PrepareDrawer(item)` returns true. That method also
  fills the drawer, and leaves it untouched for agents with nothing to show, so a closing drawer
  doesn't go blank.
  - Today: Claude Code (usage, or the setup or update offer), Codex (usage), Grok Build (week,
    extra usage credits and a pie of what it went on), Cursor CLI and Cursor desktop (the billing
    period) and GitHub Copilot (AI credits against the plan), plus their presets, matched by command name
    (`CommandName`), or for Cursor by catalog id too (`IsCursor`; the only desktop agent with a drawer).
  - Moving between two drawer agents swaps the content in place.
  - Usage is loaded on the first hover after each show and cached until the next show
    (`ClearUsageCache` in `ReloadData`), so opening stays fast.
  - Add new agents in `PrepareDrawer`.
- **Usage overview** (owner's request, 2026-10-16; `ShowSummary`, `SummaryPanel`): while no agent with a
  drawer is hovered, the drawer shows every listed agent's usage in brief. One row per account
  (`SummaryEntries`: copies share a row, presets are left out, Cursor CLI and desktop share one),
  each a button with the icon, name, and a bar per reset window (session and week when both exist,
  stacked a few DIP apart so they stay distinct) with a caption under each like "85% session ·
  resets 16:08" ("13% month · resets 10 Nov" for Cursor, "79% of AI credits · resets 1 Nov" for
  Copilot). Clicking launches the agent. Rows only for agents with usage or a check running, so
  with none the drawer stays shut.
  - Opening the flyout starts every agent's check (`StartSummaryChecks`, through the same
    `Start…Check` helpers and intervals as hovering; Claude only when asked directly) and opens the
    overview once the window is placed. A finished check rebuilds it (`RunLiveCheckAsync`, Copilot's fetch).
  - Only while **no agent** is pointed at (owner's correction, 2026-10-16: hovering Claude or ChatGPT
    desktop left it open). The close timer decides: over an agent without a drawer it closes (the
    window shrinks to the list); over no agent it shows the overview (`ShowSummary` returns false when
    it's off, on settings, with a pop-up open, or with no rows), else closes. So `ScheduleDrawerClose`
    also runs with the drawer shut when the overview is on, to bring it back after such a hover. Hovering a drawer agent swaps its content in
    with the fade (`PrepareDrawer` wraps `PrepareAgentDrawer` and hides the overview). Returning from
    settings brings it back.
  - **Suggestion** under the rows (`ShowSuggestion`, owner's request): the agent whose tightest
    active quota window is least used (a minimax score across session and week, or month for Cursor
    and Copilot). A candidate with any current 100% limit is ruled out; expired limits are fresh,
    and unlimited Copilot is unconstrained. Ties favour more longer-window headroom, then more
    session headroom. When the winner changes by at least 15 points, up to three alternatives name
    the better agent for a quick task, sustained work, or after a reset within three hours. Unknown
    reset times cannot create a reset alternative. Shown only with two or more usable candidates.
    Icons in the rows are centred vertically.
  - Settings → General → **Usage overview** (`DockState.ShowUsageSummary`, default on, missing = on).
  - Verified 2026-10-16 through UI Automation: the five rows above with the right bar states.
    **Not yet verified by eye:** the look, hover swaps, clicking a row.
- Leaving the row or the drawer starts a 250 ms close timer, so the pointer can move into the
  drawer to click Set up or a recent folder. The whole column keeps it open, including the empty
  area under the usage (`Background="Transparent"`, or that area wouldn't receive the pointer).
- **Recents** (`Services/RecentLaunches.cs`): the last five folders Claude Code, Codex, Grok Build, OpenCode, Aider, Qwen Code, DeepSeek Harness, Cursor CLI or Copilot CLI (each copy separately) was launched
  in, under the usage. Clicking one launches that agent there. Launching a folder already in the
  list moves it to the front and shifts the ones above it down. At most five are stored, and the
  drawer shows only as many as fit under the usage without scrolling. Stored in `state.json`
  (`recentLaunches`, keyed by agent id) so `agents.json` isn't rewritten. The settings page calls
  the switch "Remember recents" (`rememberRecentLaunches`, default on; a missing property stays on).
  Off hides the list and stops recording; the saved folders stay for when it's turned back on.
  Presets share their agent's list. Recorded for Claude Code, Codex, Grok Build, OpenCode, Aider, Qwen Code, DeepSeek Harness, Cursor CLI and Copilot CLI (`RecentLaunches.Records`, which takes the definition: by command name, or for Cursor CLI / DeepSeek Harness also by catalog id, since Cursor's command can be plain `agent` and DeepSeek's is `dsh`), the CLI agents with a drawer; Codex, Grok, OpenCode, Aider, Qwen Code, DeepSeek Harness, Cursor CLI and Copilot CLI added at the owner's request. The Claude, ChatGPT and Cursor desktop apps record them too (they open in a folder); other desktop apps have none.
  Each folder's context menu (right-click, Shift+F10, menu key) has "Remove from recents"
  (`RecentLaunches.Forget`). Removing the last folder closes the drawer if there's no usage to show.
- **Pop-ups hold the hover state** (`HoldWhileOpen`, `_popupsOpen`): every menu or flyout opened over
  the flyout (an agent's or preset's right-click menu, a recent folder's menu, the remove
  confirmation, the rename box) is counted while open. A pop-up takes the pointer, so the row or
  drawer sees it leave; without the hold, the drawer starts closing, the window resizes, and Windows
  closes the pop-up (the owner saw the menu vanish, the drawer close and a busy cursor, 2026-10-16).
  While any is open, the drawer, picture and banner colour stay. When the last closes, they settle as
  if the pointer had just left, unless the mouse is over a row or the drawer
  (`PointerOverRowOrDrawer`: `GetCursorPos` → `ScreenToClient` → `FindElementsInHostCoordinates`).
  Call it just before `ShowAt` (or from `Opening` for a `ContextFlyout`). Not yet verified in-app.
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
  fully open, the subject is centred across list and drawer together (it used to pin the picture's
  edge to the window's, which left her well left of centre; owner's screenshot, 2026-10-19); in between it slides with the drawer's width animation, in the same tick as
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

#### Codex usage (`Services/CodexUsage.cs`)

- **Source: Codex itself, live** (owner's decision, 2026-10-16: the only route, no setting, no
  other fallbacks). `codex -s read-only -a never app-server` is the local JSON-RPC interface Codex's
  own TUI and desktop app use (JSON lines over stdin/stdout, no `"jsonrpc"` field). Agent Dock sends
  `initialize` (clientInfo `agent-dock`), the `initialized` notification, then one
  `account/rateLimits/read` with `excludeResetCreditDetails: true`, and closes stdin (the server
  exits; a stuck one is killed with its process tree, since it's a .cmd shim → node → codex).
  - `account/rateLimits/read` is a regular method in `codex-rs/app-server-protocol`, not marked
    experimental, though `codex --help` calls `app-server` experimental as a whole. CodexBar
    (a third-party menu-bar app) uses the same route.
  - **Codex does its own sign-in and token refresh.** Agent Dock never reads `auth.json` or holds a
    token. Don't call `chatgpt.com/backend-api/wham/usage` directly instead: that would mean handling
    the token, and the owner ruled out other routes.
  - **Live and account-wide:** the numbers come from OpenAI at the moment of the check, so they
    don't depend on when Codex last ran on this PC, and they include use on other machines, the
    desktop app and cloud tasks. (The old session-log source was removed: it was only as fresh as
    the last local session, a day stale when tested.)
- **Result:** `rateLimitsByLimitId.codex` if present, else `rateLimits`; `primary` / `secondary`
  with `usedPercent`, `windowDurationMins` (300 and 10080 seen) and `resetsAt` (Unix seconds,
  nullable). Windows are assigned by length (a day or less is the session), not by name. A result
  with no windows is an empty success. `planType` becomes `PlanUsage.Plan` ("Team plan" under the
  drawer's title; "unknown" is dropped) and `credits` becomes `PlanUsage.Extra` ("Extra usage credits: …" when
  on). `CodexUsage.Parse` is pure.
- **When:** in the background on hover over Codex, only if the last answer is over 3 minutes old
  (`CodexUsage.MinInterval`); the answer is kept across shows. Nothing runs while the flyout is
  hidden. Timeouts: 10 s to start, 10 s for the reply.
- **Saving:** each success is written to `%LOCALAPPDATA%\AgentDock\codex-usage.json` (temp file +
  move). The drawer shows the last answer, else that file, straight away, and fills in when a check
  returns. On failure the saved numbers stay with a note under them ("… Showing the last saved
  limits."); with nothing saved, the note alone. Notes never include an error's text.
  `ShowCheckedUsage` is shared with Claude's direct check.
- **Verified 2026-10-16:** parser cases in a throwaway console project (deleted), and a real check
  through the owner's Codex 0.163.0: 0.9 s, team plan, both windows returned and saved, no codex
  processes left behind. The in-app drawer with it is not yet verified.
- **Verified by the owner (2026-10-15):** the Codex picture, drawer, cross-fade between agents, and
  the seam overlap.

#### Grok Build usage (`Services/GrokUsage.cs`; owner's request, 2026-10-16)

- **No CLI source.** Grok Build 1.0.60 has no usage command or call: `grok usage` is one session's
  tokens and cost, its status line gets only context and tokens, and it only learns of a limit from
  refused requests (`usage_pool_exhausted`, `usage_limit_reached`), when it links to
  `grok.com?_s=usage`. No usage in `~/.grok`. Don't read browser cookies (Chrome and Edge encrypt
  them to the browser anyway).
- **Sign-in: Grok Build's own** (owner's decision, 2026-10-16, replacing a WebView2 sign-in of Agent
  Dock's own, now deleted with `GrokSignInWindow`). `~\.grok\auth.json` holds one entry per x.ai
  session (`https://auth.x.ai::<id>`) with `key` (a JWT) and `expires_at` (ISO). grok.com accepts that
  key as `Authorization: Bearer` for the call below (tested 2026-10-16: same reply as the browser).
  - Only `key` and `expires_at` are read (`ReadToken`, `FileShare.ReadWrite`; of several sessions,
    the one expiring last). **Never refresh it** (Grok Build does; a refresh here would race it),
    never write the file, never log, show or save the token. An expired one sends nothing.
  - It lasts only hours, so between Grok Build runs the drawer often shows the saved numbers with
    "Grok Build's sign-in has expired. Run Grok Build to renew it."
  - The old `GrokUsageOfferDismissed` state and the `%LOCALAPPDATA%\AgentDock\WebView2` profile are
    no longer used (the folder isn't deleted by the app).
- **Source: grok.com, as its usage page asks.** Found by recording what that page fetched, inside
  Agent Dock (the recorder is gone):
  `POST https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig`, gRPC-web
  (`application/grpc-web+proto`, `x-grpc-web: 1`), empty request message. Undocumented.
  - Reply field 1 (the week): 1 = percent used (float), 4 / 5 = window start and end (Timestamp,
    seconds), 7 = repeated breakdown {1 = category, 2 = share (float)}, 12.1 = extra-usage credit left
    in cents (372, then 359 after use). 8, 11 and 13 aren't used. Categories, matched against the
    owner's chart: 2 Grok Build, 4 Chat, 5 Imagine, 9 Grok bot, 10 Cursor; others "Other".
  - The same page also fetches `ListPaymentMethods`, which returns the billing address. Agent Dock
    never requests it; the discovery recording that caught it was deleted.
- **The check** (`GrokUsage.FetchAsync`, off the UI thread): one `HttpClient` POST with the bearer
  token, `x-grpc-web: 1`, and an empty gRPC-web frame. The status, content type and grpc headers
  (headers or trailing headers) go to `GrokUsage.Parse`, which is pure (gRPC-web frames + a minimal
  protobuf reader). Signed out means: HTTP 401/403; grpc-status 16/7 in a trailer *or the response
  headers*; a grpc-message mentioning auth, sign or login; a reply that isn't gRPC; or an empty reply
  with no status (all seen through the old browser sign-in). At most every 5 minutes, only on hover;
  saved to `grok-usage.json`.
- **Drawer:** "Grok usage", a **Week** bar only (no session window: `showSession: false`), "Extra usage credits:
  $3.59", and a pie of the breakdown with a legend (`ShowBreakdown`, slices clockwise from twelve
  o'clock by size, tooltips). With no sign-in, a note says to sign in to Grok Build (there's no offer
  any more). The settings page says where the numbers come from.
- Verified 2026-10-16: decoder against a reply rebuilt from the recorded one (throwaway project,
  deleted); live in the owner's drawer through the old browser sign-in. The bearer route, through
  the real `GrokUsage.FetchAsync` in a throwaway console project (deleted): 100%, resets Wed 21 Oct
  11:11, $1.50, the breakdown. **Not yet verified:** the drawer with the new route, the pie on screen.

#### Cursor usage (`Services/CursorUsage.cs`; owner's request, 2026-10-16)

- **No CLI source.** Cursor CLI (2026.10.16) has no usage command; `about` / `status` give only the
  plan tier and email, and `/usage` is interactive only.
- **Sign-in: Cursor CLI's** (owner's decision): `%APPDATA%\cursor\auth.json` has `accessToken` (a JWT,
  `sub` = `auth0|user_…`, `exp` about two months out) and `refreshToken`. Only `accessToken` is read.
  Same rules as Grok's: never refresh it (the refresh token is single-use), never write the file,
  never log, show or save the token; an expired one sends nothing.
- **Source:** `GET https://cursor.com/api/usage-summary`, what cursor.com's dashboard uses, with the
  dashboard's cookie built from the token: `WorkosCursorSessionToken=<user id>%3A%3A<token>` (user id
  = `sub` after the "|"). The `HttpClient` has `UseCookies = false`, or the hand-set cookie is dropped.
  Undocumented (third-party write-ups, and tested). Without a session: 401 `not_authenticated`.
  - `api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage` (Connect JSON, POST `{}`,
    bearer token) also works and is what Cursor's clients call, but has no plan name or on-demand
    block, so it isn't used. Same percentages.
- **Reply** (owner's, Pro+): `billingCycleStart` / `billingCycleEnd` (ISO), `membershipType`
  (`pro_plus` → `PlanUsage.Plan` "pro+" → "Pro+ plan"), `individualUsage.plan` with `totalPercentUsed`,
  `autoPercentUsed`, `apiPercentUsed` (0–100) and `used` / `limit` / `breakdown` in cents (7000 included,
  9605 bonus), `individualUsage.onDemand` (`enabled`, `used`, `limit`, cents), and `teamUsage` (empty
  for an individual). `CursorUsage.Parse` is pure.
- **Drawer** (CLI and desktop: one account, `IsCursor` by catalog id or `cursor-agent`): "Cursor usage",
  plan under the title, a **Total** bar (the week slot, relabelled with `weekLabel`), then
  "Auto + Composer" and "API" bars (`ModelWeeks` with `UsageWindow.Title`), all resetting at the
  period's end; "On-demand usage: $x of $y" when on (`ExtraUsage.Dollars`). Saved to
  `cursor-usage.json`; at most every 5 minutes, only on hover. Settings shows `CursorSettings` (text
  only) on both agents' pages.
- Verified 2026-10-16 through the real `CursorUsage.FetchAsync` (throwaway console project, deleted):
  Pro+, 12.7% total, Auto + Composer 13.7%, API 1.6%, resets 10 Nov. **Not yet verified:** the drawer.

#### GitHub Copilot usage (`Services/CopilotUsage.cs`)

- **No local source:** Copilot CLI (1.0.98) keeps no quota. `~/.copilot/logs` have nothing, and
  `data.db` only has `session_context_usage` (context window). It has no usage command for scripts:
  `copilot help billing` is a help topic; usage shows in its footer, `/usage`, and its custom status
  line (`statusLine` in its config gets session JSON on stdin; not used).
- **Source (owner's decision, 2026-10-16):** `GET copilot_internal/user`, what Copilot's own clients
  call (undocumented), through `gh api` (no window, 15 s timeout, `GH_PROMPT_DISABLED`), so gh's own
  sign-in is used and Agent Dock never handles a token. **No extra scope:** it works with gh's
  `gist, read:org, repo, workflow`.
  - Reply: `token_based_billing` (true means AI credits; false, the legacy premium requests),
    `quota_reset_date` ("2026-11-01") / `quota_reset_date_utc`, `copilot_plan` (`individual_pro`),
    `access_type_sku`, and `quota_snapshots.premium_interactions` with `entitlement` (7000),
    `remaining`, `percent_remaining`, `credits_used` (5552), `unlimited`, `overage_permitted`,
    `overage_count`. `chat` and `completions` were unlimited. `CopilotUsage.Parse` is pure.
  - Plan names aren't shown: `copilot_plan` said `individual_pro` while the sku said
    `plus_monthly_subscriber_quota`, so the plan couldn't be named reliably.
  - Replaced the documented billing API (`users/{login}/settings/billing/premium_request/usage`),
    which needed gh's `user` scope (the owner's refresh never went through), only covered plans the
    user pays for, and had no allowance (the removed `monthlyAllowance` setting; old agents.json
    files with it still load, since unknown properties are ignored).
  - Don't pass a leading slash to `gh api` from Git Bash, which rewrites it into a file path.
- **Display:** one row, "AI credits" (or "Premium requests"): a bar and "5,552 of 7,000 · resets
  1 Nov"; "Unlimited · N used" or a plain count without an entitlement; "Over the plan: …" under it
  when overage is allowed. `WeekGroup` hidden. Fetched in the background on hover and cached 5 minutes
  across shows; "Checking GitHub…" until it arrives.
- Verified 2026-10-16 through the real `CopilotUsage.FetchAsync` (throwaway console project, deleted):
  5552 of 7000 AI credits, resets 2026-11-01 00:00 UTC, no overage. **Not yet verified:** the drawer.

#### Hover background (owner's request)

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
    the character about 72% of the way across (`BackdropSubjectX`); new pictures should keep that layout.  Images are cached per path; the fade is skipped when Windows animations are off; hiding the
  flyout resets it.
- **Not yet verified in-app:** loading from the embedded resources. The build embeds all 10, but
  nobody has hovered since.

#### API usage (owner's request, 2026-10-19; `Services/ApiUsage.cs`, `FlyoutWindow.ApiAccounts.cs`)

- **Accounts:** `DockSettings.ApiAccounts` in agents.json: id (`ApiUsage.NewId`: the provider, then
  "-2"…), name, provider (`ApiUsage.Providers`, an `ApiProvider` each: DeepSeek and Anthropic, which
  have a balance or spend, and since 2026-10-20 OpenAI, OpenRouter, Google Gemini and xAI, keys only:
  `ChecksBalance` false, so no checks, no drawer bar, no overview row, and `FetchAsync` says
  `NoBalanceCheck`; the flyout's `ApiAccountFor` ignores them, so a linked agent's drawer stays its own), and `apiKey`, stored as typed
  (owner: keys are not hidden from the user; settings shows them in a plain text box), or `%NAME%`
  read from the environment (`ApiUsage.KeyFor`). An agent links to one with `AgentDefinition.ApiAccount`.
- **DeepSeek:** `GET https://api.deepseek.com/user/balance` with the key as a bearer token
  (api-docs.deepseek.com, "Get User Balance"): `is_available`, and `balance_infos` per currency with
  `total_balance` as a string. USD is preferred. 401/403 is "not accepted". No limit or spend in it.
- **The percentage** (owner's choice): of `ApiBalanceState.Full`, in state.json `apiBalances` by account
  id with the last balance. The first balance sets it; a rise of more than half a cent is a top-up and is
  added to it, so earlier use still counts; it never sits below the balance. Settings' **Balance after
  your last top-up** + **Set** overrides it ("I paid $20, it first saw $19.57"). `is_available: false`
  counts as 100% used.
- **Checks:** `StartApiCheck`, at most every 5 minutes (`ApiUsage.MinInterval`), when the overview
  opens, on hovering a linked agent, and on opening settings; **Check now** forces one. Results stay
  across shows (`_apiChecks`); balances are saved. A key or service change drops the check (and a
  service change the balance).
- **Shown:** the overview has a row per account (`ApiSummaryEntry`; a linked agent's row is the
  account's, so copies share it), with the service's logo (DeepSeek Harness's icon) when no agent is
  linked; clicking launches the first linked agent, or opens settings. In the suggestion a balance is a
  long window ("balance"); an unlinked key isn't suggested, and a key two or more listed agents are
  linked to is named itself, with them ("Suggested: DeepSeek (Aider, OpenCode)", `SuggestedName`;
  owner, 2026-10-19), since any of them uses the same balance. Settings → Accounts → **Suggest plans
  before API keys** (`DockState.PreferPlansOverApi`, on by default, missing = on; owner's request): while
  any plan agent is usable, the suggestion and its alternatives come only from plan agents, so
  included usage goes first; API keys are suggested once no plan has room. The two-candidate rule
  counts before that narrowing. A linked agent's drawer shows one bar,
  "42% · $5.80 left of $10.00" (`ShowApiUsage`), before its own usage logic, then its recents.
- **Anthropic** (owner's request, 2026-10-19): no balance API (the Console shows it; claude-code issue
  #47574), so the Admin API's documented Cost Report, `GET /v1/organizations/cost_report` with
  `x-api-key` and `anthropic-version: 2023-06-01`, daily buckets (at most 31 a page, through
  `next_page`), amounts in cents. Needs an Admin key (`ApiAccount.AdminKey`, `sk-ant-admin…`; checked
  before sending); `ApiKey` is the ordinary key for Aider. `ApiUsage.ReportsSpend`: `Spent` since
  `ApiBalanceState.Since` (0 = this month); 100% only once the user sets the Console's balance
  (`Full`, `Since` = then; spending from that day, so its earlier spend counts). No top-up
  detection. Without it: "$12.40 spent since 1 Oct", no bar, not in the suggestion. Logo: Claude's.
  The Spend Limits API is Claude Enterprise members' limits, not a Console balance.
- **Agents on a key** (owner's requests: Aider 2026-10-19; every agent that can take it 2026-10-20,
  with the Accounts redesign): `ApiUsage.EnvironmentFor` adds the variable from
  `ApiUsage.KeyVariableFor(agent, provider)` = the account's `ApiKey` as written (`%NAME%` stays a
  reference) to the agent's env at launch (`Launcher`, all launch paths), unless its own env sets it.
  `KeyVariableFor`, from each tool's documentation and **not yet verified by a launch** except Aider:
  Aider and OpenCode any service (OpenCode's Gemini variable is `GOOGLE_GENERATIVE_AI_API_KEY`),
  DeepSeek Harness DeepSeek only (`DEEPSEEK_API_KEY`, what its LLM and web plugins default to), Crush
  all but DeepSeek, Goose Anthropic / OpenAI / OpenRouter / Gemini (`GOOGLE_API_KEY`), Qwen Code
  OpenAI, Claude Code Anthropic, a command of the user's own (no catalog id, command not a known
  agent's) any; desktop apps and plan agents (Codex, Grok Build, Cursor CLI, Copilot CLI, and the
  rest) never. Claude Code was Aider-only's reason (a key moves it from its plan to API billing); it's
  now offered with that said on the key page and the agent page (`LeavesPlan`), its Claude usage
  panel hides while linked, and Accounts leaves it out of Plans. The agent page says which variable
  it gets (`AgentApiNote`).
- **Saving spend** (fixed 2026-10-20): both windows saved a check only when it had a `Balance`, so
  Anthropic's `Spent` never reached state.json; they save either now.
- **Linking offer** (owner's choice B): the agent page's **Account** choice (agents that take keys,
  or are linked) and an InfoBar ("Use … 's key?") when `ApiUsage.AgentUses`: Aider's model starts with `deepseek/` or
  `deepseek-`, OpenCode's `~\.local\share\opencode\auth.json` has a `deepseek` entry (only the names
  are read), or the agent is DeepSeek Harness. For Anthropic also Claude Code with `ANTHROPIC_API_KEY`
  in its env, and Aider on a `claude…` model.
- **Verified 2026-10-19:** parser cases and the 100% logic in a throwaway project (deleted); a real
  call gets DeepSeek's 401 for a bad key; through UI Automation, adding an account, the key saved, the
  "not accepted" status, **Set** saving 20, and the overview row. **Not yet:** a real balance (the
  owner's key needed), the drawer, the agent page, removing through the UI.

#### Taskbar icon (owner's request, 2026-10-19; `Services/TaskbarIcon.cs`)

- **The choice:** Settings → General → Taskbar icon, a `GridView` (`TaskbarIconPicker`) under Waifu
  backgrounds. Agent Dock's own icon (`default`, the exe's `Assets\AgentDock.ico`), seven plain logos,
  and with waifus on the host character (`waifu-dock-1..3`) and one waifu per known agent, named after
  it. Only icons compiled in are offered. One choice overall, whatever the waifu mode (owner's decision).
  `SingleSelectionFollowsFocus="False"`: each choice rewrites the pin, so arrowing through mustn't
  (with it on, a choice changed by itself once in testing).
- **Artwork:** 1024 px sources in `Assets\AppIcons\appicon-<id>.png` (made by the owner's image
  generator from a brief; not shipped). `tools\Build-AppIcons.ps1` crops each to what's drawn (the
  sources have a 6% border, which looked small next to other taskbar icons; owner, 2026-10-19),
  then writes `Assets\AppIcons\ico\<id>.ico` with PNG frames at 16, 20, 24, 32, 40, 48, 64 and 256 px
  (stepwise halving, light unsharp mask at 48 px and below). Those are committed and compiled in as
  `AppIcons.<id>.ico` (about 3.7 MB). Rerun the script after changing a source.
- **Applying:** the pinned button is the pin's shortcut (Agent Dock's window is hidden from the
  taskbar), so `TaskbarIcon.Apply` writes the icon to `%LOCALAPPDATA%\AgentDock\TaskbarIcons\<id>-<hash>.ico`
  and sets it on every pin of this exe (`TaskbarPins.ShortcutsTo` / `SetIcon`: load, `SetIconLocation`,
  save in place, so the pin stays). The default sets an empty location (the target's icon, as when
  Windows pins it). The hash is of the icon's contents, because Windows' icon cache survives Explorer
  restarts and is keyed by path; older files there are deleted. The jump list's Quit entry uses the
  same file (`UpdateJumpList`). Not pinned: the picker is disabled with a note.
- **Showing it:** Windows doesn't redraw a pinned icon (see "Things learned the hard way"), so after a
  choice an InfoBar offers **Restart now** (`TaskbarIcon.RestartExplorer`: closes this session's
  explorer.exe, which Windows restarts by itself; starts it by hand after 5 s). Otherwise it shows at
  the next sign-in. The offer stays until a restart (`_taskbarIconPending`).
- **Waifus off:** a waifu icon switches to Agent Dock's own, and the waifu one is kept in
  `DockState.TaskbarWaifuIcon`; turning waifus on brings it back if no other icon was chosen in between
  (my recommendation, not yet confirmed by the owner). The choice is `DockState.TaskbarIcon` (null =
  default).
- **Verified 2026-10-19** through UI Automation and a capture of the taskbar button: all 32 choices
  listed, choosing sets the shortcut and state, waifus off/on as above (8 choices with waifus off), and
  **Restart now** showed Grok's waifu on the taskbar, the same size as the icons beside it, with Agent
  Dock still running. **Not yet verified:** the picker's look, keyboard use, the not-pinned note.

### Possible next steps (none requested yet; ask before starting)

- ARM64 build in CI (needs ARM64 MSVC tools on the runner for Native AOT cross-compilation).
- More of the settings page. It exists (gear in the footer): general settings, the agent list
  (add, remove, duplicate, rename, arguments, environment variables, drag to reorder), and per-agent
  pages. Only Claude Code has real settings there; Codex and Copilot have none.
- Usage for the agents not done yet (see "Agents: where each one stands" and `tasks.md`), and for the Claude and
  ChatGPT desktop apps from their CLI counterparts' accounts.

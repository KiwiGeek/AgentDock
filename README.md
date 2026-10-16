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
| Gear button (footer) | Settings, including your agents (add, rename, duplicate, remove). Esc there goes back to the list |
| Right-click an agent (or Shift+F10) | **Save as preset** for the current folder (command line agents), **Settings**, or **Remove from list** |
| Right-click a preset | Rename or remove it |
| Drag the top edge (the bottom edge if your taskbar is at the top) | Makes the list taller or shorter. Double-click the edge to fit the list again |
| Esc, click elsewhere, or click the taskbar icon again | Closes the flyout. In settings, Esc goes back one level first (an agent's page, then the list) |

### Presets

A preset is a command line agent tied to a folder, for work you come back to again and again.
Presets sit at the top of the flyout and in the taskbar icon's right-click menu, and always open
in their own folder, whatever the working folder is set to. A preset's name starts as the folder
name; right-click it to rename it.

They're kept in `%APPDATA%\AgentDock\presets.json`. Agent Dock rewrites that file when you save,
rename or remove a preset, so comments there aren't kept, but you can edit it by hand:

```json
{
  "presets": [
    { "name": "Agent Dock", "agent": "claude", "folder": "W:\\AgentDock", "arguments": "--continue" }
  ]
}
```

`agent` is an agent id from `agents.json`. `arguments` is optional and replaces that agent's own
arguments. A preset whose folder has gone shows dimmed so you can remove it.

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

## Your agents

The flyout shows only the agents you've chosen. The first time it opens, it offers to **Find
agents**: Agent Dock looks for the agents it knows about (Claude Code, Codex, Gemini CLI, GitHub
Copilot CLI, OpenCode, Aider, Qwen Code, Cursor, Amp, Kiro, Goose, Crush, Factory Droid, Grok
Build, Hermes Agent, DeepSeek Harness, and the Claude, ChatGPT, Copilot, Perplexity and Cursor
desktop apps), lists the ones installed, and adds the ones you leave ticked. **Not now** starts
you with an empty list.

After that, manage them on the settings page (the gear in the footer), under **Agents**:

- **Scan for new agents** looks again for agents Agent Dock knows; **Add your own command** takes
  any other command line tool (a name, a command, and optional arguments).
- Click an agent for its page: rename it, change its arguments, set **environment variables**,
  move it up or down, **duplicate** it, or remove it.
- Or right-click an agent in the flyout (or press Shift+F10) for **Settings** (its page) and
  **Remove from list**.
- **Duplicate** makes a second copy of the same agent with its own name and variables, for example a
  work and a personal Claude Code account: give the copy `CLAUDE_CONFIG_DIR` (or `CODEX_HOME` for
  Codex) pointing at its own folder, and sign in once inside it. Plan usage in the drawer is shown
  only for the default account for now; a copy set up for another account says so.
- Environment variables are set in the terminal before the agent starts. Write `%NAME%` in a value
  to use a variable from Windows, so a key never has to be written into `agents.json`.

When an agent Agent Dock knows is installed later, the flyout offers it once ("Amp is installed.
Add it?"). It's never added without asking, and closing the offer means it isn't offered again
(it can still be added from **Add agents**).

## Configuration

`%APPDATA%\AgentDock\agents.json` holds your agent list and a few settings. Agent Dock writes it
when you change agents in settings, so comments in it aren't kept, but it's plain JSON you can still
edit by hand (the pencil button opens it). If a hand edit breaks it, Agent Dock says so in the footer
and won't save over it until it's fixed; deleting it starts you again with the welcome.
**Hide unavailable agents** on the settings page hides agents in your list that aren't installed
any more, rather than showing them dimmed.

```jsonc
{
  "terminal": "auto",        // auto (Windows Terminal if present) | wt | console
  "shell": "auto",           // auto (pwsh, else Windows PowerShell) | pwsh | powershell | cmd
  "hideUnavailable": true,
  "maxRecentFolders": 8,
  "startWithWindows": true,  // start hidden at sign-in so the first click is fast
  "agents": [
    // CLI: resolved against PATH. "catalog" is the known agent it was added from (icon, picture, colour).
    { "id": "claude", "name": "Claude Code", "command": "claude", "catalog": "claude" },
    // A second account: its own config folder (%NAME% is read from Windows' environment)
    { "id": "claude-2", "name": "Claude Code (work)", "command": "claude", "catalog": "claude",
      "env": { "CLAUDE_CONFIG_DIR": "%USERPROFILE%\\.claude-work" } },
    { "id": "claude-resume", "name": "Claude Code (continue)", "command": "claude", "arguments": "--continue" },
    { "id": "codex-cmd", "name": "Codex", "command": "codex", "shell": "cmd" },
    // Your own icon: .png, .ico, .jpg, .bmp, or an .exe to take it from (relative to this folder)
    { "id": "aider", "name": "Aider", "command": "aider", "icon": "icons/aider.png" },
    // Your own picture to fade in behind the flyout while the pointer is over this agent (and its
    // presets), instead of the built-in one
    { "id": "aider", "name": "Aider", "command": "aider", "background": "my-aider.png" },

    // The colour the banner on the left takes on while this agent is highlighted
    { "id": "goose", "name": "Goose", "command": "goose", "color": "#4C6B3A" },

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

### Claude usage limits

If you use Claude Code with a Pro or Max plan, the flyout can show how much of your 5-hour session
and weekly limits you've used, and when each resets, in a drawer that slides out beside the
flyout while the pointer is over Claude Code. Where those numbers come from is up to you: open the
settings page (the gear in the footer), choose **Claude Code**, and pick under **Usage limits**.

**Read them from the status line** (the default). Click **Set up** in the drawer the first time. Agent Dock then
becomes Claude Code's status line, the supported way to get these numbers. It reads only what
Claude Code passes to its status line, never your login. The status line itself shows something
like `Opus 5.5 | 5h 37% | 7d 12%`. Setting up replaces any status line you already had, and backs
up Claude Code's `settings.json` first, next to the original (`settings.json.agentdock-<date>.bak`).
Claude Code only refreshes its status line while a session is running, so the flyout says how old
the numbers are. If you move Agent Dock, the flyout notices and offers to update the path.

**Ask Claude for them directly.** The drawer checks your limits itself when you hover over Claude Code,
at most every three minutes, so the numbers are current even when no session is running. It makes
the same call Claude Code's `/usage` command makes, which is **not a published Anthropic API** and
can stop working whenever Anthropic changes it. To make it, Agent Dock reads the access token in
Claude Code's own `.credentials.json` (or `CLAUDE_CODE_OAUTH_TOKEN` if you set it). It never
refreshes that sign-in, never changes the file, and never saves or shows the token. If the sign-in
has expired, open Claude Code once and it renews it. Anthropic's terms describe subscription sign-ins
as being for Claude Code and claude.ai, which is why this is off unless you choose it. If a check
fails, the drawer keeps showing the last saved numbers and says why. A status line you set up
earlier keeps working alongside it.

The drawer also lists the last five folders you opened Claude Code in, under the usage. Click one
to open Claude Code there again. Opening a folder that's already in the list moves it to the top
and shifts the others down. At most five are kept, and only as many as fit in the drawer.
Right-click a folder there (or press Shift+F10) and choose **Remove from recents** to drop it.
On the settings page, **Remember recents** turns the list off, and **Clear recents** forgets every
folder (it asks first). Those choices, the usage source, and the folders are saved in `state.json`.

### Codex usage limits

Codex gets the same drawer while the pointer is over it, with nothing to set up. Agent Dock asks
Codex itself for your 5-hour and weekly limits, through the same local interface Codex's own apps
use (`codex app-server`, started read-only and closed straight after). Codex uses its own sign-in,
so Agent Dock never reads your login or handles a token. The numbers are live and cover your whole
account, including Codex on other computers, the desktop app and cloud tasks, whether or not you've
used Codex on this PC lately. It checks at most every three minutes, only while you're hovering
over Codex. If a check fails (no network, or Codex needs you to sign in again with `codex login`),
the drawer keeps showing the last numbers it got and says why.

### GitHub Copilot usage

Copilot's drawer shows the premium requests you've used this month, which reset at 00:00 UTC on
the 1st. Copilot keeps no copy of that on your PC, so Agent Dock asks GitHub's billing API through
the GitHub CLI (`gh`), using gh's own sign-in. That API needs one extra permission, which you grant
once:

```powershell
gh auth refresh -h github.com -s user
```

GitHub reports only how many requests you've used, not your plan's allowance. To get a bar instead
of a count, add your plan's monthly allowance to Copilot's entry in `agents.json`, for example
`"monthlyAllowance": 300` for Copilot Pro. It only works for plans you pay for yourself; usage of a
plan provided by an organisation isn't available this way. Results are cached for five minutes.

### Icons

Icons for the built-in agents are compiled into Agent Dock. They match by agent id (`claude`,
`codex`, `gemini`, `copilot`, `opencode`, `aider`, `qwen`, `cursor-cli`, `amp`, `kiro`, `goose`,
`crush`, `droid`, `grok`, `hermes`, `deepseek`, `claude-desktop`, `chatgpt-desktop`, `copilot-desktop`,
`perplexity`), or for CLI agents by command name, so `"command": "claude"` gets the Claude icon
under any id. An `icon` setting overrides that. Desktop apps without either use the icon the Start
menu shows. Anything else gets a plain terminal or app symbol.

Hover pictures are built in the same way, as `Assets\Agents\<agent id>-background.png` (currently
Aider, Amp, ChatGPT, Claude, Claude Code, Codex, Copilot, Copilot desktop, Crush, Cursor, Cursor CLI,
DeepSeek, Factory Droid, Gemini, Goose, Grok, Hermes, Kiro, OpenCode, Perplexity and Qwen), matched
by agent id or command name. A `background` setting overrides one. Turn them off with **Waifu
backgrounds** on the settings page (the gear in the footer), which also hides the hearts on the
banner. That choice is saved in `state.json`.

The banner down the left of the flyout takes on the colour of whichever agent is highlighted: a
colour picked for its picture while waifu backgrounds are on, or one taken from its logo when they're
off. Black and white logos keep your Windows accent colour. A `color` setting overrides either.

`state.json` in the same folder holds recent folders, the last few folders Claude Code was opened
in, cached app ids, and whether waifu backgrounds and recent folders are on. Start menu icons are
cached under `%LOCALAPPDATA%\AgentDock\Icons`; delete that folder if an app's icon changes.

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
| `Assets/Agents` | Built-in agent icons, compiled into the exe as resources |

# Agent Dock

A Windows 11 taskbar flyout for launching AI agents, built with WinUI 3. Click the pinned icon
and an acrylic flyout opens above it; pick an agent and it starts. CLI agents open in Windows
Terminal in the working folder you've chosen (DeepSeek Harness is hosted in the background
instead, with a Stop button on its row). Desktop agents just launch.

Right-clicking the taskbar icon gives the same list as a jump list, which launches straight into
the last folder you used without opening the flyout.

## Install

Download `KiwiGeek.AgentDock-win-x64-Setup.exe` (or `-win-arm64-` on an ARM PC) from the latest
[release](https://github.com/KiwiGeek/AgentDock/releases/latest) and run it. It installs for you
only, without an admin prompt, and adds Agent Dock to the Start menu. Then pin it (see below).

Agent Dock updates itself: it checks GitHub a few times a day, downloads a new version in the
background, and switches to it the next time it starts. **Settings → About** shows the version you're
running and what the updater is doing, with **Check for updates**, and **Restart now** once a new
version is downloaded. It also has links to the release notes and issues, a **Copy** button with the
version details for a bug report, and buttons that open the folders your settings and data live in.
With waifu backgrounds on, the waifu portraits drift past behind it. Uninstall it from
**Settings → Apps**.

The rest of this section is for building it yourself. To work on Agent Dock, see
[BUILDING.md](BUILDING.md) instead: it covers the Debug build and the build-and-run script.

## Requirements

- Windows 11
- .NET 10 SDK
- Visual Studio's **Desktop development with C++** workload (or the standalone C++ build tools).
  Native AOT uses the MSVC linker. If you'd rather skip it, set `<PublishAot>false</PublishAot>`
  in the project file; everything still works, the hand-off is just a little slower.

The Windows App SDK runtime is bundled into the output, so nothing else needs installing.

## Build and pin

To build and run a copy of your own instead of installing a release:

```powershell
dotnet publish src\AgentDock\AgentDock.csproj -c Release -r win-x64 -o C:\Tools\AgentDock
```

Use `-r win-arm64` on an ARM PC. A copy built this way doesn't update itself (only an installed
copy does), and it's the same Agent Dock as an installed one: same settings, and only one of the two
runs at a time. A Debug build is different: it's `AgentDock.Dev`, with its own settings, pin and
flyout, so it runs beside either (see [BUILDING.md](BUILDING.md)).

Then run `C:\Tools\AgentDock\AgentDock.exe`. If it isn't pinned
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
| Click an agent, or press its key (1–9, then A–Z) | Launches it and closes the flyout |
| Arrow keys, Enter | Move through the list and launch |
| Folder drop-down / folder button | Pick a recent folder or browse for a new one |
| Terminal button (footer) | Plain shell in the working folder, no agent |
| Pencil button (footer) | Opens `agents.json` |
| Gear button (footer) | Opens the settings window (closing the flyout): left list of General, Accounts, each agent, and Add agents; the right pane shows that item's details. Esc closes the window |
| Chat button on the Claude, ChatGPT or GitHub Copilot row | Opens the app for a plain chat, without the working folder (see below) |
| Right-click an agent (or Shift+F10) | **Save as preset** for the current folder (agents that open in a folder), **Settings**, or **Remove from list**. Claude also has **New chat** and **New Cowork session**, ChatGPT and GitHub Copilot **Open without a folder** |
| Right-click a preset | Rename or remove it |
| Drag the top edge (the bottom edge if your taskbar is at the top) | Makes the list taller or shorter. Double-click the edge to fit the list again |
| Drag the right edge | Makes the list wider. Past about 790 DIP each section's rows wrap into two columns, and the drawer opens further right. Double-click the edge to go back to one column |
| Esc, click elsewhere, or click the taskbar icon again | Closes the flyout |

### Presets

A preset is an agent tied to a folder, for work you come back to again and again: any command line
agent, or the Claude, ChatGPT or Cursor desktop app.
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
shims work and you land back at a prompt in the folder when the agent exits. DeepSeek Harness
is different: Agent Dock runs `dsh web` itself (no Terminal tab), opens the browser UI on the
folder you chose (as a Harness workspace), and shows **Stop** on the row while it's running.
With **Waifu backgrounds** on, her picture also becomes the Harness page background.
For every CLI agent, Agent Dock clears an inherited `NO_COLOR` setting and corrects `TERM=dumb`
inside its Terminal tab, so the agent can render its normal colours. Environment variables set
explicitly on an agent's page still take precedence. Crush can use local Ollama models: add an
Ollama provider in Crush's own `crushrc` and select a model from Crush's model picker. Agent Dock
does not configure or charge for the model.

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
agents**: Agent Dock looks for the agents it knows about (Claude Code, Codex, Antigravity CLI, GitHub
Copilot CLI, OpenCode, Aider, Qwen Code, Cursor, Amp, Kiro, Goose, Crush, Factory Droid, Grok
Build, Freebuff, Hermes Agent, DeepSeek Harness, and the Claude, ChatGPT, GitHub Copilot, Microsoft Copilot,
Perplexity and Cursor desktop apps), lists the ones installed, and adds the ones you leave ticked. **Not now** starts
you with an empty list.

After that, manage them in the settings window (the gear in the footer), under **Agents**:

- **Scan for new agents** looks again for agents Agent Dock knows; **Add your own command** takes
  any other command line tool (a name, a command, and optional arguments). Agents it knows but
  couldn't find are under **Not found on this PC**: tick one to add it anyway, for example before
  you install it. It shows as not installed until Agent Dock can find it, and it's looked for
  again every time the flyout opens.
- Click an agent for its page: rename it, choose the **account** (API key) it runs on, change its
  arguments, set **environment variables** (folded away; an account covers keys), **duplicate** it,
  or remove it. Drag agents in the list (here or in the flyout
  itself) to put them in the order you want.
- An Aider entry also has an optional **Model name**. Agent Dock starts it with
  `--model provider/model`; choose its key under **Account**, and put other Aider configuration in
  Aider itself. Its **Find a model** search runs Aider's local `--list-models` catalogue query and lets
  you select the returned identifier; it is not a check of which models your account can use.
  Duplicate the entry when you want a second ready-to-use model.
- **General** is in groups: **Flyout** (usage overview, hiding unavailable agents, waifu
  backgrounds), **Taskbar icon**, **Launching** (terminal, shell, **Close terminal when agent
  exits**, which closes Agent Dock's tab after a CLI agent finishes instead of leaving a prompt, and
  **Start with Windows**) and **Recent folders**.
- Turning **Waifu backgrounds** on asks you to confirm, a little differently each time. Turning
  them off doesn't.
- CLI agents opened in Windows Terminal get a tab titled **Agent · folder**, the agent's icon, and
  (when Agent Dock knows the agent's colour) a matching tab tint, via hidden app-owned profiles in
  Terminal's Fragments folder. With **Waifu backgrounds** on, those tabs also get the selected
  agent's picture. Turning Waifus off removes only the pictures; icon and colour stay. Your other
  profiles are left alone. If Terminal has not loaded a profile yet, the agent opens with title and
  colour from the launch line, but without the icon until the next try.
- **Taskbar icon** picks the picture on Agent Dock's pinned taskbar button: Agent Dock's own, seven
  plain logos, or (with Waifu backgrounds on) a waifu for Agent Dock or any agent it knows.
  Windows only redraws a pinned icon when File Explorer restarts, so Agent Dock offers
  **Restart now** (which closes any open File Explorer windows); otherwise it changes at your next
  sign-in. Turning Waifu backgrounds off switches a waifu icon back to Agent Dock's own, and
  turning them on again brings it back, unless you've picked another icon in between.
- Or right-click an agent in the flyout (or press Shift+F10) for **Settings** (its page) and
  **Remove from list**.
- **Duplicate** makes a second copy of the same agent with its own name and variables, for example a
  work and a personal Claude Code account: give the copy `CLAUDE_CONFIG_DIR` (or `CODEX_HOME` for
  Codex) pointing at its own folder, and sign in once inside it. The drawer then shows that
  account's own usage, and the copy's settings page has its own Claude usage choice. A copy using
  an API key or another endpoint has no plan, so its drawer says plan limits don't apply.
- Environment variables are set in the terminal before the agent starts. Write `%NAME%` in a value
  to use a variable from Windows, so a key never has to be written into `agents.json`.

When an agent Agent Dock knows is installed later, the flyout offers it once ("Amp is installed.
Add it?"). It's never added without asking, and closing the offer means it isn't offered again
(it can still be added from **Add agents**).

## Configuration

`%APPDATA%\AgentDock\agents.json` holds your agent list and a few settings (a Debug build keeps its
own in `%APPDATA%\AgentDock.Dev`, so it can run beside an installed copy). Agent Dock writes it
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
earlier keeps working alongside it. Asking directly also shows any per-model weekly limits (such as
Sonnet's) as extra bars, and your extra usage when it's turned on. Under the title, the drawer names
the model your last session used, when the status line has reported it.

The drawer also lists the last five folders you opened Claude Code in, under the usage (Codex's, Grok Build's, Freebuff's, OpenCode's, Aider's, Qwen Code's, Crush's, Factory Droid's, Kiro CLI's, DeepSeek Harness's, Cursor CLI's and GitHub Copilot CLI's drawers do the same for them). Click one
to open the agent there again. Opening a folder that's already in the list moves it to the top
and shifts the others down. At most five are kept, and only as many as fit in the drawer.
Right-click a folder there (or press Shift+F10) and choose **Remove from recents** to drop it.
On the settings page, **Remember recents** turns the list off, and **Clear recents** forgets every
folder (it asks first). Those choices, the usage source, and the folders are saved in `state.json`.

While the pointer isn't over an agent with usage, the drawer shows a **usage overview**: every
agent in your list that has usage to show, one line each, with a bar for each reset window
(session and week when both apply) and when that window resets ("85% session · resets 16:08"),
so you can pick one with room left without hovering the agent first. Under the rows,
it suggests the agent whose tightest active limit is least used - balancing its session and its
weekly, monthly or daily allowance instead of ignoring either. When another agent is materially better for
a quick task, sustained work, or after a limit reset within three hours, the overview names that
alternative too. It does not rank agents by model quality, speed or price.
Click a line to open that agent. Opening the flyout checks them all in the background (no more often than hovering
would). Turn it off with **Usage overview** on the settings page.

### Codex usage limits

Codex gets the same drawer while the pointer is over it, with nothing to set up. Agent Dock asks
Codex itself for your 5-hour and weekly limits, through the same local interface Codex's own apps
use (`codex app-server`, started read-only and closed straight after). Codex uses its own sign-in,
so Agent Dock never reads your login or handles a token. The numbers are live and cover your whole
account, including Codex on other computers, the desktop app and cloud tasks, whether or not you've
used Codex on this PC lately. It checks at most every three minutes, only while you're hovering
over Codex. If a check fails (no network, or Codex needs you to sign in again with `codex login`),
the drawer keeps showing the last numbers it got and says why. Your plan shows under the title
("Team plan"), and your credit balance when you have credits.

### Grok Build usage

Grok Build doesn't report your usage itself; only grok.com does (Settings → Usage). So Agent Dock
asks grok.com the way its usage page does, signed in as Grok Build is: it reads Grok Build's sign-in
from `~\.grok\auth.json` (read only; Agent Dock never renews it or changes the file). There's nothing
to set up beyond signing in to Grok Build.

Hovering over Grok Build shows your **week**: how much of the weekly pool you've used and when it
resets, your remaining extra-usage **credits**, and a pie chart of what the usage went on (Imagine, Cursor, Grok
bot, Grok Build, Chat). It checks at most every five minutes, only while you're hovering. Grok Build's
sign-in only lasts a few hours and Grok Build renews it itself, so between uses the drawer may show
the last numbers it got and say the sign-in has expired. That isn't a published xAI API, so it can
stop working when grok.com changes; the drawer then keeps the last numbers it got and says why.

### Freebuff usage

Hovering over Freebuff shows today's **hours**: how many of the day's allowance are left (30 on a
full-access account, 5 in limited-access countries) and when it resets. Freebuff counts them in
Freebucks internally, five to an hour; Agent Dock shows hours. It asks Freebuff's own session
endpoint, signed in as Freebuff CLI is: it reads the CLI's token from Windows Credential Manager
(or, from older CLIs, `credentials.json`) read only, never refreshes it or changes the file, and
never saves or shows the token. Sign in once with `freebuff login`. It checks at most every five
minutes, only while you're hovering. That endpoint isn't published, so it can stop working; the
drawer then keeps the last numbers it got and says why.

Under the hours are the free tier's **sessions**: how many of the day's 5, the week's 14 and the
month's 40 are used. Sessions are Freebuff's other limit, and each paid plan adds more of them
alongside its hours. How fast the hours go depends on the model: Freebuff prices each one in hours
per hour, and its model list calls GLM 5.3 Flash, DeepSeek V4.1 Flash, MiMo v2.6 Flash Fast, Solar
Mini 4 and Solar Pro 4 unmetered at full access, so they cost no hours. Gemini 3.8 Flash and Muse
Spark 1.3 need a paid plan, and GPT-6.1 Sol and Gemini 4 Argon (outside the US) are one session a
day.

### Cursor usage

Hovering over Cursor CLI or the Cursor desktop app (one account, so both show the same numbers)
shows your billing period, as cursor.com's dashboard does: the **Total** used and when it resets, then
the **Auto + Composer** and **API** pools, your plan, and on-demand spend when it's on. Agent Dock asks
cursor.com signed in as Cursor CLI is: it reads the CLI's sign-in from `%APPDATA%\cursor\auth.json`
(read only), so sign in to Cursor CLI once (`cursor-agent login`). It checks at most every five
minutes, only while you're hovering. Not a published Cursor API, so it can stop working.

### Kiro usage

Hovering over Kiro CLI shows your **monthly credits**: how many you've used of the plan's allowance,
when they reset, and the plan name. Agent Dock runs `kiro-cli chat --no-interactive /usage` and
reads its report, so Kiro CLI's own sign-in is used and no token is stored. Sign in once with
`kiro-cli login`. It checks at most every five minutes, only while you're hovering.

### GitHub Copilot usage

Copilot's drawer shows this month's usage against your plan: AI credits (or premium requests, on
GitHub's older billing) used out of what the plan includes, and when it resets. Copilot keeps no
copy of that on your PC, so Agent Dock asks GitHub the way Copilot's own apps do, through the
GitHub CLI (`gh`) with gh's own sign-in. Sign in to gh once (`gh auth login`); no extra permission
is needed. This isn't a published GitHub API, so it can stop working. Results are cached for five
minutes. (The `monthlyAllowance` setting of earlier versions is no longer used.)

### Accounts and API keys

Settings → **Accounts** lists everything your agents run on, in one place:

- **Plans**: the sign-ins Agent Dock reads usage through (Claude, ChatGPT, GitHub, Cursor, Kiro, Grok, Freebuff),
  each with the agents that share it. Nothing is stored for these; choose one to open its agent's
  usage settings.
- **API keys**: a key is added once (**Add a key**: a name, the service, and the key itself, or
  `%DEEPSEEK_API_KEY%` to read it from an environment variable) and then handed to every agent you
  switch on for it, on the key's own page under **Agents that use this key**, or from the agent's
  page under **Account**. Services: DeepSeek, Hyper, Anthropic, OpenAI, OpenRouter, Google Gemini and xAI.
  Agent Dock can read DeepSeek and Hyper balances and Anthropic spending; the others are keys only.

Keys are kept in `agents.json` as you type them:

```json
"apiAccounts": [
  { "id": "deepseek", "name": "DeepSeek", "provider": "deepseek", "apiKey": "sk-..." },
  { "id": "anthropic", "name": "Anthropic", "provider": "anthropic", "apiKey": "sk-ant-api...", "adminKey": "sk-ant-admin..." }
]
```

**An agent on a key needs no variables.** Agent Dock starts it with the variable that agent reads,
set to the account's key: `ANTHROPIC_API_KEY`, `DEEPSEEK_API_KEY`, `OPENAI_API_KEY`,
`OPENROUTER_API_KEY`, `GEMINI_API_KEY` or `XAI_API_KEY` (OpenCode reads Gemini's as
`GOOGLE_GENERATIVE_AI_API_KEY`, Goose as `GOOGLE_API_KEY`). Agents that can take a key: Aider and
OpenCode (all but Hyper), DeepSeek Harness (DeepSeek), Crush (all but DeepSeek), Goose (Anthropic,
OpenAI, OpenRouter, Gemini), Qwen Code (OpenAI), commands of your own (any), and Claude Code
(Anthropic: this moves it off your Claude plan to API billing, and settings says so). A variable of
the same name on the agent's own page wins.

For Crush's native Hyper sign-in, add a **Hyper** account under Accounts and leave its API key
blank, then select that account on Crush's page. Agent Dock reads Crush's current sign-in token
only for Hyper's read-only credit balance request. It never refreshes or saves the token and sends
nothing when it has expired. A Hyper API key can be entered instead; then linked Crush launches
with `HYPER_API_KEY`. Hyper reports the number of Hypercredits left, not a plan limit or reset time,
so Agent Dock shows the balance without a percentage bar.
Crush uses the heart from its official artwork in the agent list; Hyper uses its own H mark in
Accounts and the usage overview.

**Anthropic has no balance API**, only spending, through the Admin API's cost report, which needs an
**Admin API key** (`sk-ant-admin…`, from an organization's Console settings; individual accounts
don't have one). An ordinary key can't read it, and an Admin key can't run models, so an Anthropic
account has both: the **API key** for Aider and the **Admin API key** for the spending. Until you
type your **balance now, from the Console** and choose **Set**, it shows what was spent this month;
after that, the balance left and a percentage, with spending counted from that day (the report is
by day, so that day's earlier spending counts too). Set it again after a top-up.

Each key with a balance gets a line in the usage overview, like "42% used · $5.80 left", and the
drawer of every agent using it shows it too (`"apiAccount": "deepseek"` on the agent in agents.json).
When Aider's model is a `deepseek/` one, OpenCode has a DeepSeek key, or the agent is DeepSeek
Harness, the agent's page offers to use it.

A prepaid balance has no limit to measure against, so the percentage is of the balance after your
last top-up: the first balance Agent Dock sees counts as 100%, and a later rise is taken as a top-up
and added to it. If that's off (you paid $20 but it first saw $19.57), type the real amount under
**Balance after your last top-up** and choose **Set**.

The overview's suggestion uses your plans' included usage first: with **Suggest plans before API
keys** on (the default, on the Accounts page), it suggests an agent on an API key only once no agent
on a plan has room left. The balance comes from DeepSeek's documented
`GET /user/balance`, at most every five minutes while the flyout is open.

### Icons

Icons for the built-in agents are compiled into Agent Dock. They match by agent id (`claude`,
`codex`, `antigravity`, `copilot`, `opencode`, `aider`, `qwen`, `cursor-cli`, `amp`, `kiro`, `goose`,
`crush`, `droid`, `grok`, `freebuff`, `hermes`, `deepseek`, `claude-desktop`, `chatgpt-desktop`,
`github-copilot-desktop`, `microsoft-copilot`, `perplexity`, `cursor`), or for CLI agents by command name, so `"command": "claude"` gets the Claude icon
under any id. An `icon` setting overrides that. Desktop apps without either use the icon the Start
menu shows. Anything else gets a plain terminal or app symbol.

Hover pictures are built in the same way, as `Assets\Agents\<agent id>-background.png` (currently
Aider, Amp, Antigravity CLI, ChatGPT, Claude, Claude Code, Codex, GitHub Copilot CLI, GitHub Copilot,
Microsoft Copilot, Crush, Cursor, Cursor CLI,
DeepSeek, Factory Droid, Freebuff, Goose, Grok, Hermes, Kiro, OpenCode, Perplexity and Qwen), matched
by agent id or command name. A `background` setting overrides one. With waifus on, the same picture
also sits behind that agent's page in the settings window. Turn them off with **Waifu backgrounds**
on the settings page (the gear in the footer), which also hides the hearts on the banner. That
choice is saved in `state.json`.

The banner down the left of the flyout takes on the colour of whichever agent is highlighted: a
colour picked for its picture when it has one, even if waifu backgrounds are hidden, or one taken
from its logo otherwise. Black and white logos keep your Windows accent colour. A `color` setting
overrides either.

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
- **The Claude and ChatGPT desktop apps open in the working folder**, like a command line agent,
  through their own documented links: Claude starts a new Claude Code session there
  (`claude://code/new?folder=`), ChatGPT a new chat with the folder as its workspace
  (`codex://threads/new?path=`). Claude asks you to confirm the folder each time, and if it's
  already open, Windows versions of Claude currently add the folder to the open window instead of
  opening a new one. Both can have presets and recent folders, and their drawers show the same
  plan usage as Claude Code and Codex (one line each in the usage overview).
- **They can also open for work that isn't coding**, from the chat button on their row, their
  right-click menu, or the jump list. Claude opens a new chat (`claude://claude.ai/new`) or a new
  Cowork session (`claude://cowork/new`). ChatGPT has no documented link for a plain chat (all its
  links are Codex's), so it just opens the app, showing whatever it showed last. None of these use
  the working folder or add to recent folders.
- **GitHub Copilot (the desktop app) opens the working folder too**, as a new session there. GitHub
  has no link for a local folder, so Agent Dock runs Copilot CLI's own `copilot app` in the folder,
  hidden (without Copilot CLI it just opens the app). It has presets and recent folders, and the
  same AI credits as Copilot CLI (one line in the usage overview). Its chat button opens the app
  without a folder.
- **Cursor opens the working folder too**, as a workspace (Agent Dock runs `Cursor.exe` with the
  folder, as Cursor's own `cursor .` command does). If that folder is already open, Cursor switches
  to its window. Presets and recent folders work for it as well.
- **Native AOT friendly throughout**: `x:Bind` only (no reflection bindings), source-generated
  JSON, and source-generated COM interop (`LibraryImport`, `GeneratedComInterface`) for the shell,
  icon extraction and the jump list.

The app is in `src/AgentDock`:

| Path | Contents |
| --- | --- |
| `Program.cs` | Entry point: forwards to the running instance, or becomes it |
| `App.xaml(.cs)` | The background instance; handles show / launch / quit requests |
| `Views` | The flyout (split into partial files by concern), the settings window, the pin prompt |
| `Controls` | The grips that drag the flyout's height and width |
| `Models` | Config/state classes, JSON context, and the `AgentItem` view model |
| `Services` | Settings I/O, IPC, autostart, jump list, taskbar icon, updates |
| `Services/Agents` | The known agents and which is which, resolving them, their icons, colours and recent folders |
| `Services/Launching` | Starting agents in Windows Terminal or hosted, its profiles, PATH lookup |
| `Services/Usage` | Each agent's plan usage, API key balances, and the overview's suggestion |
| `Interop` | Win32 and shell COM declarations, icon extraction, Start menu enumeration |
| `Assets` | Built-in agent icons and pictures, taskbar icons and portraits, compiled into the exe as resources |

`tools/` has the scripts that build the taskbar icons and portraits, and the DeepSeek Harness plugins.

## Licence

Copyright (C) 2026 Joshua Penman.

Agent Dock is free software: you can redistribute it and/or modify it under the terms of the
[GNU General Public License](LICENSE), version 3 or (at your option) any later version. It is
distributed in the hope that it will be useful, but without any warranty; see the licence for
details.

**Third-party marks.** The agent icons in `Assets/Agents`, and any product logos that appear in
the hover pictures, belong to their owners. They are included only to identify each agent, and the
GPL grants no rights to them. AGENTS.md ("Done: agent icons") records where each icon came from.

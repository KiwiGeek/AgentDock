# Tasks

Work agreed with the owner but not started. Remove an item when it's done (and record the result
in `AGENTS.md`, and tick the agent there).

## Agent status

Which agents are fully done (usage, recents, icons, verified) is the checklist in `AGENTS.md`.
Done as of 2026-10-16: Claude Code and Claude desktop, Codex, Grok Build, Cursor CLI and Cursor
desktop, GitHub Copilot CLI. Everything below is still to do.

## Review the agents Agent Dock knows (added 2026-10-16)

These are in `Services/KnownAgents.cs` (offered by Find agents, not added by default). For each:
confirm the command (or Start menu name), keep or remove it, and look for a usage source.

| Id | Name | Command / Start menu name | Hover background | Notes |
| --- | --- | --- | --- | --- |
| `antigravity` | Antigravity CLI | `agy` | no | Google's replacement for Gemini CLI (added 2026-10-16). Not installed on the owner's PC, so not verified to launch. Has an icon and a picture. Usage: its quota is shared with the Antigravity IDE; no source found yet |
| `opencode` | OpenCode | `opencode` | yes | Recent folders added 2026-10-17. No universal usage bars: it can use arbitrary providers. Still to verify launch and recents after installation. |
| `aider` | Aider | `aider` | yes | Model setting, Aider-driven model search and recent folders added 2026-10-18. No usage bars: it can use arbitrary providers. Still to verify launch, model search and recents after installation. |
| `qwen` | Qwen Code | `qwen` | yes | Recent folders added 2026-10-19. No usage source yet. Still to verify launch and recents after installation. |
| `amp` | Amp | `amp` | yes | Sourcegraph |
| `kiro` | Kiro CLI | `kiro-cli` | yes | Recents and monthly credit usage via `kiro-cli chat /usage` (2026-10-21). Still to verify by eye. Formerly Amazon Q Developer CLI (`q`); decide whether `q` needs its own entry |
| `goose` | Goose | `goose` | yes | Block |
| `crush` | Crush | `crush` | yes | Charm. Recents and optional native Hyper balance added 2026-10-21. Verify the colourful TUI from the flyout, recents, Hyper balance, and local Ollama tool use. No universal usage bars: Crush supports local and many paid providers. |
| `droid` | Factory Droid | `droid` | yes | Recent folders and orange brand mark added 2026-10-21. No usage source yet. Still to verify launch and recents. |
| `deepseek` | DeepSeek Harness | `dsh web` | yes | DeepSeek's own agent (github.com/deepseek-ai/deepseek-harness). Hosted in the background (`HostedAgents`, no Terminal; Stop on the row). Subtitle "Opens in the browser". API key (`DEEPSEEK_API_KEY`) and recents wired 2026-10-20. Launch folder opened as a Harness workspace via `tools/dsh-plugins` (2026-10-20). Still to verify: launch, Stop, and the folder in the UI. |
| `hermes` | Hermes Agent | `hermes` | yes | Nous Research. A general agent, not just coding. Installed natively on the owner's PC. Bring-your-own API key, so a candidate host for DeepSeek |
| `grok` | Grok Build | `grok` | yes | Done (usage, recents). Still to check: is there a Grok desktop app? |
| `freebuff` | Freebuff | `freebuff` | no | Added 2026-10-23: icon, hover picture (`freebuff-background.png`), daily hours from the CLI's sign-in, recents. Still to verify an actual launch and recents. |
| `claude-desktop` | Claude | Start menu "Claude" | yes | Done: opens a Claude Code session in the working folder (`claude://code/new?folder=`), presets, recents, and Claude Code's usage |
| `chatgpt-desktop` | ChatGPT | Start menu "ChatGPT" | yes | Opens a new chat in the working folder (`codex://threads/new?path=`), presets, recents, and Codex's usage. Still to confirm by an actual launch |
| `cursor` | Cursor | Start menu "Cursor" | yes | Usage (Cursor CLI's account), and opens the working folder as a workspace (`Cursor.exe <folder>`), presets, recents. Still to confirm the folder launch by an actual launch |
| `github-copilot-desktop` | GitHub Copilot | Start menu "GitHub Copilot" | yes | Opens a session in the working folder (via `copilot app`), presets, recents, Copilot CLI's usage; chat button opens it without a folder. Still to verify by eye: the drawer and picture. No waifu taskbar icon of its own (owner: not needed for now) |
| `microsoft-copilot` | Microsoft Copilot | Start menu "Copilot" | yes | Launch only. Renamed from `copilot-desktop` / "Copilot" on 2026-10-19 (older entries are migrated). Not installed on the owner's PC |
| `perplexity` | Perplexity | Start menu "Perplexity" | yes | Launch only |

Verify Windows Terminal waifu tab backgrounds (and icons/colours) in the running app; close-on-exit is already good.

Answered: Cursor CLI's command is still `cursor-agent` (2026.10.16), with `agent` as an alias;
`IsCursor` and `RecentLaunches.Records` match it by catalog id too.

## Taskbar icon picker: follow-ups

Built 2026-10-19 (see "Taskbar icon" in `AGENTS.md`). Left:

- Confirm with the owner: turning waifus back on restores the earlier waifu icon (built that way,
  on my recommendation), and Agent Dock's own icon is the plain default.
- A pinned shortcut that's re-created (unpinned and pinned again) gets the exe's icon back, and
  settings would still show the old choice. Possibly re-apply the choice when the pin prompt sees a new pin.
- Fallback if an Explorer restart ever doesn't show it: set `PKEY_AppUserModel_RelaunchIconResource`
  on the pin prompt window, so re-pinning picks up the chosen icon.

## Maybe later: profiles for API credits

Decided 2026-10-16: DeepSeek is covered by its own agent, DeepSeek Harness (above), so this is
deferred. It's for when the owner wants a provider inside a host agent instead, e.g.
"OpenCode · DeepSeek": one entry that runs an existing agent (OpenCode, Aider, Crush, Goose,
Hermes, ...) configured for another provider and model. Alternatives looked at: Deep Code
(`deepcode`, community-made, listed in DeepSeek's API docs).

Things to decide:

- Which host agent to use, and whether that's per entry (e.g. `"host": "opencode"`).
- How the provider and model are passed: command-line arguments, environment variables, or the
  host agent's own config file.
- API keys go in `agents.json` as they are (owner's decision, 2026-10-19: a tool for programmers,
  their own keys aren't hidden from them), or a `%NAME%` environment variable.
- The entry should use DeepSeek's icon and background while launching the host agent.
- The balance part is built: API keys in settings, linked to agents (see "API usage" in `AGENTS.md`).

## API usage: follow-ups

- **Verify against a real DeepSeek key.** The parser and the 100% logic were checked with sample
  replies; the only real reply so far was a 401 (OpenCode's stored "deepseek" key was rejected).
- **Verify Anthropic against a real Admin key** (only the parser and a bad-key reply are checked),
  and Aider actually running on a bound key.
- More services: OpenRouter (`GET /api/v1/key` gives a real limit and usage, so no top-up guessing),
  Moonshot/Kimi. OpenAI's billing needs an organisation admin key, like Anthropic's.
- Not yet verified by eye: the settings section, the agent page's choice and offer, the drawer, the overview row.
- Accounts redesign (2026-10-20): verify by a real launch that each agent `ApiUsage.KeyVariableFor`
  names actually picks the key up (OpenCode, Crush, Goose, Qwen Code, Claude Code), and by eye the
  Accounts rows and the key page's switches. Plan rows could show usage bars (the flyout's saved
  numbers). A "Find a setting" box in the left list was in the design and isn't built.

## Maybe later: custom groups

Discussed 2026-10-16 and deferred. The flyout's groups are by launch method (command line vs
desktop), not purpose. If three or more general (non-coding) command-line agents turn up, add an
optional `"group"` setting in `agents.json` so the user can make their own headings, e.g.
"General agents". This means reworking the fixed Presets / Command line / Desktop lists, number keys,
Up/Down and jump list categories into a dynamic set of groups.

## Left over from earlier sessions

- CI: `.github/workflows/build.yml` and `RELEASING.md` were never committed.
- The flyout never gets `WS_EX_TOPMOST` (see "Verify first" in `AGENTS.md`), so without foreground
  rights it sits behind other windows.

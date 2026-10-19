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
| `qwen` | Qwen Code | `qwen` | yes | |
| `amp` | Amp | `amp` | yes | Sourcegraph |
| `kiro` | Kiro CLI | `kiro-cli` | yes | Formerly Amazon Q Developer CLI (`q`). Decide whether `q` needs its own entry |
| `goose` | Goose | `goose` | yes | Block |
| `crush` | Crush | `crush` | yes | Charm |
| `droid` | Factory Droid | `droid` | yes | |
| `deepseek` | DeepSeek Harness | `dsh web` | yes | DeepSeek's own agent (github.com/deepseek-ai/deepseek-harness, first released 2026-08-13, developer preview). `web` serves a UI at 127.0.0.1:3080 and opens the browser; the terminal keeps the server running. Install with `npm i -g @deepseek-ai/dsh`. Check: does it use the launch folder as its workspace? Where does the API key go? |
| `hermes` | Hermes Agent | `hermes` | yes | Nous Research. A general agent, not just coding. Installed natively on the owner's PC. Bring-your-own API key, so a candidate host for DeepSeek |
| `grok` | Grok Build | `grok` | yes | Done (usage, recents). Still to check: is there a Grok desktop app? |
| `claude-desktop` | Claude | Start menu "Claude" | yes | Done: opens a Claude Code session in the working folder (`claude://code/new?folder=`), presets, recents, and Claude Code's usage |
| `chatgpt-desktop` | ChatGPT | Start menu "ChatGPT" | yes | Opens a new chat in the working folder (`codex://threads/new?path=`), presets, recents, and Codex's usage. Still to confirm by an actual launch |
| `cursor` | Cursor | Start menu "Cursor" | yes | Usage (Cursor CLI's account), and opens the working folder as a workspace (`Cursor.exe <folder>`), presets, recents. Still to confirm the folder launch by an actual launch |
| `copilot-desktop` | Copilot | Start menu "Copilot" | yes | Launch only. Microsoft Copilot, not GitHub's |
| `perplexity` | Perplexity | Start menu "Perplexity" | yes | Launch only |

Windows Terminal waifu profiles now use the shared Fragments folder (not a settings `"import"`).
Verify their appearance in the running app; close-on-exit is already good.

Answered: Cursor CLI's command is still `cursor-agent` (2026.10.16), with `agent` as an alias;
`IsCursor` and `RecentLaunches.Records` match it by catalog id too.

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
- **Where the API key lives.** It must not go in `agents.json` in plain text. Options: an
  environment variable the user sets, the host agent's own credential store, or Windows Credential
  Manager.
- The entry should use DeepSeek's icon and background while launching the host agent.
- Possibly usage or balance in the drawer, if DeepSeek's API offers it.

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

# Tasks

Work agreed with the owner but not started. Remove an item when it's done (and record the result
in `CLAUDE.md`).

## Review the agents Agent Dock knows (added 2026-10-16)

These are in `Services/KnownAgents.cs` (offered by Find agents, not added by default). For each:
confirm the command (or Start menu name), and keep or remove it.

| Id | Name | Command / Start menu name | Hover background | Notes |
| --- | --- | --- | --- | --- |
| `aider` | Aider | `aider` | yes | |
| `qwen` | Qwen Code | `qwen` | yes | |
| `cursor-cli` | Cursor CLI | `cursor-agent` | yes | Check whether it's been renamed to `cursor` |
| `amp` | Amp | `amp` | yes | Sourcegraph |
| `kiro` | Kiro CLI | `kiro-cli` | yes | Formerly Amazon Q Developer CLI (`q`). Decide whether `q` needs its own entry |
| `goose` | Goose | `goose` | yes | Block |
| `crush` | Crush | `crush` | yes | Charm |
| `droid` | Factory Droid | `droid` | yes | |
| `deepseek` | DeepSeek Harness | `dsh web` | yes | DeepSeek's own agent (github.com/deepseek-ai/deepseek-harness, first released 2026-08-13, developer preview). `web` serves a UI at 127.0.0.1:3080 and opens the browser; the terminal keeps the server running. Install with `npm i -g @deepseek-ai/dsh`. Check: does it use the launch folder as its workspace? Where does the API key go? |
| `hermes` | Hermes Agent | `hermes` | yes | Nous Research. A general agent, not just coding. Installed natively on the owner's PC. Bring-your-own API key, so a candidate host for DeepSeek |
| `grok` | Grok Build | `grok` | yes | Renamed from "Grok CLI" by the owner (2026-10-16). Installed at `~/.grok/bin/grok.exe`. Also check for a Grok desktop app |
| `cursor` | Cursor (desktop) | Start menu "Cursor" | yes | The IDE |

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
- GitHub Copilot usage: the owner reported it "did not work". See `CLAUDE.md`.

# WIP: Windows Terminal waifu backgrounds

This branch preserves the unfinished Windows Terminal integration requested on 2026-10-18.
Agent Dock itself is built and running, but the waifu image still does not appear in the launched
Terminal tab. Do not present this feature as complete.

## Goal

When **Waifu backgrounds** is on, a CLI agent launched through Windows Terminal should use that
agent's Agent Dock background picture. The same global setting should control whether the temporary
Terminal profiles exist. A separate **Close terminal when agent exits** option is also implemented:
it uses a non-persistent PowerShell or cmd session, so the Agent Dock tab closes when the command
returns.

## What was found

- The owner uses Microsoft Store Windows Terminal `1.25.2733.0`, with settings at
  `%LOCALAPPDATA%\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json`.
- Store Terminal ignores the unpackaged fragment location
  `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\AgentDock`. That route is for web/unpackaged
  apps; a Store app needs an app-extension manifest to contribute fragments, which Agent Dock does
  not have.
- The first implementation also used the reflection JSON serializer. Native AOT rejected it while
  launching, so images could be copied without a profile JSON being written. The current code uses
  `DockJsonContext` source generation and no longer breaks launches.
- The documented modular-settings alternative is a top-level `"import"` entry in Terminal's
  settings.json. The current code writes exactly this app-owned line:

  ```json
  "import": ["AgentDock-profiles.json"],
  ```

  It writes `AgentDock-profiles.json` next to `settings.json`, with hidden profile entries and
  absolute image paths under Agent Dock's data folder. This import and file were inspected and are
  present on the owner's machine.
- Despite that, launching an agent after the reload grace period still opens a normal terminal tab
  with no background. `wt --profile <name>` returns exit code zero both for an Agent Dock profile
  and a deliberately nonexistent profile, so it is not a useful recognition check.

## Current safeguards

- Agent Dock waits two seconds after writing the imported file before passing `--profile`; before
  that it launches an ordinary tab, preventing profile startup failures from blocking agents.
- Turning Waifu backgrounds off removes Agent Dock's imported file, the one import line if it is
  unchanged, copied images, and the obsolete unpackaged fragments. Existing Terminal profiles are
  not rewritten.
- If a user already has their own `"import"` setting, Agent Dock declines to alter it and opens
  ordinary tabs. Extending an existing JSONC import array safely is future work.

## Best next investigation

1. Check whether Windows Terminal parses `AgentDock-profiles.json` when reached through `import`.
   In particular, verify whether an import requires main-settings shape
   `"profiles": { "list": [...] }`, rather than the fragment shape `"profiles": [...]`.
2. Add a deterministic profile GUID if Store Terminal's imported-profile code requires one, even
   though the fragment documentation says a name is the minimum required field.
3. Use a visible, throwaway profile only in a controlled test to inspect Terminal's effective
   profile list. Do not automate a terminal window, and remove that test profile immediately.
4. Keep the safe fallback until a real Agent Dock launch visibly uses the background.

## Files involved

- `Services/TerminalProfiles.cs`: creation, import insertion/removal, image cleanup and launch
  readiness gate.
- `Models/Settings.cs`: source-generated Terminal fragment model and close-on-exit setting.
- `Services/Launcher.cs`: profile selection and non-persistent shell mode.
- `FlyoutWindow.xaml` / `FlyoutWindow.xaml.cs`: general setting and preparation when Waifus change.


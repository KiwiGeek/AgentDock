# WIP: Windows Terminal waifu backgrounds

Status as of the 2026-10-18 fix attempt: the earlier `"import"` approach was wrong. Store Terminal
**does** load `%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\<app>\` (WSL and VSDebugConsole
already use it on this PC). The official schema has no top-level `"import"` key.

## Goal

When **Waifu backgrounds** is on, a CLI agent launched through Windows Terminal should use that
agent's Agent Dock background picture. The same global setting controls whether the temporary
Terminal profiles exist.

## What failed before

- Writing `"import": ["AgentDock-profiles.json"]` into settings.json. Terminal ignores unknown keys,
  so profiles never loaded; `wt --profile` still exits 0 for missing names.
- Assuming Store Terminal ignores the unpackaged Fragments folder. It does not.

## Current approach (`Services/TerminalProfiles.cs`)

1. Write `Fragments\AgentDock\profiles.json` with fragment shape `"profiles": [ … ]`, stable GUIDs,
   `hidden: true`, agent `icon` and `tabColor` when known, `suppressApplicationTitle`, and (when
   Waifus are on) absolute `backgroundImage` paths next to the JSON. Launch also passes `--title`,
   `--suppressApplicationTitle`, and `--tabColor` so tabs stay named and tinted even when the
   profile is not ready yet; the icon only appears once `--profile` can be used.
2. Touch `settings.json`'s mtime so Terminal reloads fragments (file changes alone do not).
3. After a real fragment change, wait ~400 ms before `--profile` only if Windows Terminal is
   already running (hot reload). Unchanged fragments on a flyout re-show are ready immediately;
   if Terminal is not running, a cold start loads fragments with no wait.
4. On Waifus off: delete the fragment folder, scrub `source: AgentDock` stubs Terminal wrote into
   settings.json, and remove any leftover mistaken import line / sidecar from the old attempt.

## Still to verify by eye

- Toggle Waifus on, wait two seconds, launch a CLI agent, confirm the background appears.
- Toggle Waifus off and confirm fragments and stubs are gone.

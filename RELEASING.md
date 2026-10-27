# Releasing Agent Dock

A release is a version tag pushed to GitHub. Everything after that is automatic: GitHub Actions
builds x64 and ARM64, packages them with [Velopack](https://velopack.io), and publishes a GitHub
release. Installed copies find it there and update themselves.

## Cut a release

1. Make sure `master` has everything you want in the release, and is pushed:

   ```powershell
   git status
   ```

   ```powershell
   git push origin master
   ```

   Then check that the CI run for that push passed (a few minutes after pushing). The newest run
   should say `completed  success`:

   ```powershell
   gh run list --workflow ci.yml --branch master --limit 1
   ```

   If it failed, fix that first: the release would build the same code.

2. Pick the version. Use `MAJOR.MINOR.PATCH` (for example `2.1.0`): bump PATCH for fixes, MINOR for
   new features, MAJOR for big changes. Add a suffix such as `-beta.1` for a test release; it's
   marked as a pre-release, and installed copies ignore it.

3. Tag the commit and push the tag (replace `2.1.0` with your version, keeping the `v`):

   ```powershell
   git tag v2.1.0
   ```

   ```powershell
   git push origin v2.1.0
   ```

4. Watch it build: **Actions** tab on GitHub → **Release**. It takes about 6 minutes. When it
   finishes, the release is under **Releases**, with notes listing the changes since the last tag.

You don't edit `<Version>` in `AgentDock.csproj`; the version comes from the tag.

## What a release contains

For each architecture (`win-x64`, `win-arm64`):

| File | What it's for |
|---|---|
| `KiwiGeek.AgentDock-win-x64-Setup.exe` | The installer. Installs for the current user, no admin prompt. |
| `KiwiGeek.AgentDock-win-x64-Portable.zip` | Runs from any folder without installing (doesn't update itself). |
| `KiwiGeek.AgentDock-2.1.0-win-x64-full.nupkg` | The whole app, for updates from older versions. |
| `KiwiGeek.AgentDock-2.1.0-win-x64-delta.nupkg` | Only what changed since the previous release (not on the first one). |
| `releases.win-x64.json` | The update feed installed copies read. |

Each file is about 100 MB except the delta and the feed. v1.0.0 (2026-10-26), the first release,
took about 6 minutes from tag to published.

## How updates reach people

- Setup installs to `%LOCALAPPDATA%\KiwiGeek.AgentDock\current\AgentDock.exe`. That path stays the
  same across updates, so the taskbar pin and the sign-in entry keep working.
  (`KiwiGeek.AgentDock`, not `AgentDock`, because `%LOCALAPPDATA%\AgentDock` already holds Agent
  Dock's data, and uninstalling deletes the install folder.)
- The running copy checks GitHub a minute after it starts and then every 6 hours, and downloads a
  newer release in the background.
- The download is applied the next time Agent Dock starts: at sign-in, or after **Quit Agent Dock**
  and a click on the taskbar icon.
- Each copy stays on the architecture it was installed with.

## If something goes wrong

- **The build failed:** open the failed run in the Actions tab and read the red step. Fix it on
  `master`, then move the tag to the fixed commit and push it again:

  ```powershell
  git tag -f v2.1.0
  ```

  ```powershell
  git push -f origin v2.1.0
  ```

  If a half-made release appeared under Releases, delete it on GitHub first.
- **A bad release went out:** don't delete it (copies that already updated need a newer version to
  move to). Fix the problem and release a higher version.

## Not set up yet

- **Code signing.** Releases are unsigned, so Windows SmartScreen warns on first run. Options:
  SignPath Foundation (free for open source) or Azure Trusted Signing (about $10 a month). `vpk pack`
  signs everything once given the details.
- **winget.** The first submission to `microsoft/winget-pkgs` is by hand; after that the
  `winget-releaser` action can open the update PR for each release.

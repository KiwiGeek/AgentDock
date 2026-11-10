# Releasing Agent Dock

A release is a version tag pushed to GitHub. Everything after that is automatic: GitHub Actions
builds x64 and ARM64, packages them with [Velopack](https://velopack.io), and publishes a GitHub
release. Installed copies find it there and update themselves.

## Cut a release

1. Make sure everything you want in the release is merged into `master` on GitHub. `master` is
   protected, so changes only get there through pull requests that passed CI; you can't push to it
   directly. Then bring your own copy of `master` up to date, since that's the commit you'll tag:

   ```powershell
   git checkout master
   ```

   ```powershell
   git pull
   ```

   ```powershell
   git status
   ```

   `git status` should say your branch is up to date with `origin/master` and there's nothing to
   commit. Then check that the CI run for the last merge passed (a few minutes after merging). The
   newest run should say `completed  success`:

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

You don't edit `<Version>` in `src/AgentDock/AgentDock.csproj`; the version comes from the tag.

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

## Code signing

Releases are signed with **Azure Artifact Signing** as *Joshua Penman* (an individual, validated
identity). The release workflow does it by itself; there's nothing to do when you tag. What it
signs: `AgentDock.exe`, Velopack's own exes and `Setup.exe`. The Windows App SDK's DLLs already
carry Microsoft's signature and are left alone (`tools\Sign-File.ps1` skips anything already
signed). The **Check signatures** step fails the release if anything that ships isn't signed.

SmartScreen trusts the name, not the certificate (which Azure replaces every few days), so the
"Windows protected your PC" warning fades over the first releases rather than at once.

### What's where

| Piece | Where |
|---|---|
| Signing account | `webclient-signing` (East US, `https://eus.codesigning.azure.net/`), resource group `webclient`, subscription *Visual Studio Enterprise*, tenant *joshua penman* |
| Certificate profile | `JoshuaPenman` (Public Trust) |
| The repo's copy of those names | `tools\signing.json` |
| What GitHub signs as | App registration **AgentDock release signing** (no secret). It trusts only GitHub tokens from `KiwiGeek/AgentDock`'s `release` environment (subject `repo:KiwiGeek@22453722/AgentDock@1421009224:environment:release`: GitHub names the owner and repo by id as well, so a re-created repo of the same name doesn't match), and has the *Artifact Signing Certificate Profile Signer* role on `JoshuaPenman` only |
| GitHub side | Settings → Environments → `release`: runs only from `v*` tags, and holds the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` |

### Signing on your own PC

You only need this to test signing. Your own Azure account has the signer role too. Sign the
Azure CLI in to the signing tenant (it asks for MFA):

```powershell
az login --tenant 279c7e63-9394-4a0f-9376-85fc754d5506
```

Then add this to a `vpk pack` command, as the workflow does:

```powershell
--signTemplate "pwsh -NoProfile -ExecutionPolicy Bypass -File W:\AgentDock\tools\Sign-File.ps1 {{file}}"
```

### If signing fails in a release

- **`azure/login` fails ("no matching federated identity" or similar):** the run didn't come from
  the `release` environment, or a tag outside `v*`. Check the environment's tag rule. The error
  quotes the subject GitHub presented; it has to match the app registration's federated credential
  (Entra ID → App registrations → AgentDock release signing → Certificates & secrets → Federated
  credentials) exactly. Moving or renaming the repo changes it.
- **403 while signing:** the app registration lost its role on the `JoshuaPenman` profile.
- **The identity validation expired:** Azure asks you to renew it once a year; renew it in the
  portal (signing account → Identity validations), then re-run the failed release job.
- Fix it, then re-run the failed jobs from the Actions tab (no need to move the tag).

## Not set up yet

- **winget.** The first submission to `microsoft/winget-pkgs` is by hand; after that the
  `winget-releaser` action can open the update PR for each release.

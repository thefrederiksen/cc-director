# Releasing CC Director

## Versioning

CC Director uses [Semantic Versioning](https://semver.org/):

- **MAJOR** (X.0.0) -- Breaking changes
- **MINOR** (0.X.0) -- New features, backward compatible
- **PATCH** (0.0.X) -- Bug fixes

Pre-release tags: append `-rc1`, `-rc2`, etc. for release candidates. Pre-releases are NOT marked as "Latest" on GitHub.

## Release Process

> The authoritative run-book is the `release-manager` skill
> (`.claude/skills/release-manager/SKILL.md`). This page is the short form; where the two differ,
> the skill wins.

The release gate runs on MERGED `main`, after the version bump has landed and before the tag - see
step 3. Running it any earlier is pointless: the bump itself would be untested by it, and a run
against a pull-request head is not a run against the squashed commit that gets tagged.

### 1. Bump the version

The version lives in ONE place - `<Version>` in `Directory.Build.props` at the repository root.
Every project derives its assembly versions from it, and no project may declare its own.

(The two csproj files this page used to name, `src/CcDirector.Wpf/CcDirector.Wpf.csproj` and
`tools/cc-director-setup/cc-director-setup.csproj`, no longer exist. Editing "both csproj files"
has been impossible for some time.)

### 2. Land it on main through a pull request

`main` is protected and cannot be pushed to directly, so the bump reaches it the same way every
other change does: a pull request titled `release: v<version> - <one-line summary>`, merged once
its ordinary local gate is green and the review is clean. Stage the file you changed by name -
never `git add -A`, which sweeps up whatever else is in a shared checkout.

```bash
git add Directory.Build.props
git commit -m "release: vX.Y.Z - <one-line summary>"
```

Note also that `docs/public/release-notes/v<version>.md` must already be merged to `main` before
you tag: the workflow publishes it verbatim and FAILS without it, burning a build on a tag that
cannot be un-pushed.

### 3. The release gate - MANDATORY, and local

On MERGED `main`, at the exact commit you are about to tag - park the checkout there first with
`git checkout main && git pull` - run all of this and let it finish:

```powershell
.\scripts\test-local.ps1 -Parked -Configuration Release
dotnet test tools/cc-director-setup.Tests/ -c Release
dotnet test tools/cc-director-setup-engine.Tests/ -c Release
```

`-Parked` adds `Gateway.Tests` and `Core.Tests`, which the ordinary gate skips.
`-Configuration Release` matches what is actually shipped; the script defaults to Debug.
The two installer projects are not in `cc-director.sln`, so `test-local.ps1` never runs them -
and this release publishes `cc-director-setup.exe`, so skipping them ships the installer untested.

Nothing waits on continuous integration here or anywhere else (CLAUDE.md 5a). This local run is
the gate instead, and unlike an ordinary change a release cannot be fixed forward - the release
workflow runs no tests, and a pushed tag cannot be un-pushed. If it is red, fix it forward and
run the gate again before tagging.

### 4. Tag the gated commit and push the tag

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

Only the TAG is pushed. `main` already carries the bump - it arrived through the merged pull
request in step 2 - and cannot be pushed to directly in any case.

Tags without `-` in the suffix (e.g., `v1.2.0`) become the "Latest" release on GitHub. Tags with `-rc` (e.g., `v1.2.0-rc1`) become pre-releases.

### 5. Wait for the release build to produce the artifacts

This is the ONE wait that remains, and it is not a test gate - it is the build that produces the
downloadable files, so there is nothing to release until it finishes. The rule against waiting on
continuous integration (CLAUDE.md 5a) is about the test job gating a merge; it does not apply here.

The GitHub Actions workflow (`.github/workflows/release.yml`) will:

1. Build `cc-director.exe` (self-contained, single-file)
2. Build `cc-director-setup.exe` (self-contained, single-file)
3. Build all cc-tools as zip archives
4. Create a GitHub Release with all artifacts attached

### 6. Verify

1. Go to the [Releases page](https://github.com/thefrederiksen/devthrottle/releases)
2. Confirm the new release is marked "Latest" (if not a pre-release)
3. Confirm these assets are attached:
   - `cc-director.exe`
   - `cc-director-setup.exe`
   - `cc-*.zip` (one per tool)

## Testing the Setup Wizard

### Fresh install test

1. Temporarily rename `%LOCALAPPDATA%\cc-director\bin` to `bin.bak`
2. Run `cc-director-setup.exe`
3. Should show "Welcome to CC Director", "Installing", "Setup Complete"
4. Rename `bin.bak` back when done

### Update test

1. With cc-director already installed, run `cc-director-setup.exe`
2. Should show "Update CC Director", "Updating", "Update Complete"
3. PATH note should be hidden (already set from first install)

### Release download test

1. Download `cc-director-setup.exe` from the GitHub Releases page
2. Run it -- should fetch the latest release and install/update

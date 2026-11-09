# Release workflow: from a change in this repo to a version in uvs

This file answers one question: *I changed something in SceneManagement, how does it reach the uvs project?* Written 2026-10-26 after a full day was lost rediscovering it.

## Where the code lives

- `uvs-package-creator` is a Unity project. Each package is its own git repository checked out under `Assets/<Package>/` (SceneManagement, AudioSystems, UniversalPlayer, EventSystem, …), so Unity compiles and tests them as project code while git tracks each one separately.
- This repository's remote is `https://github.com/lejeanf/SceneManagement.git` (Jean-François' GitHub). There is no GitLab mirror; the uvs GitLab never sees this code, only the published package.
- The published package is `fr.jeanf.scenemanagement` on the public npm registry (`registry.npmjs.com`, scope `fr.jeanf`). uvs resolves it through the `jeanf` scoped registry in `Packages/manifest.json`.

## Versions are cut by CI, never by hand

`.github/workflows/publish.yml` runs semantic-release on every push to `main` or any `prerelease/*` branch. semantic-release reads the commit messages since the last release, decides the next version, writes it into `package.json` in a `chore(release): x.y.z [skip ci]` commit, tags it, packs a signed tarball with the UPM CLI and publishes it with `npm publish` (`.github/publish-unity.sh`).

- Do not edit the `version` field in `package.json`. The release commit does.
- The commit type decides the bump (`.releaserc.json`): `fix:` and `chore:` → patch, `feat:` → minor, a `BREAKING CHANGE:` footer → major. Anything else (`docs:`, `ci:`, `refactor:`, a plain message) does not release at all, so a change that must ship needs a `fix:`/`feat:`/`chore:` subject.
- The branch decides the npm dist-tag: `main` → `latest` (version `x.y.z`), `prerelease/alpha` → `alpha` (version `x.y.z-alpha.N`). A branch named `prerelease/foo` would publish on tag `foo`.
- Publishing needs the repo secrets (UPM service account, Unity org id, npm token). Pushing is enough; nothing runs locally.

## Which branch to push to

Push where uvs is consuming from. `uvs/Packages/manifest.json` names the exact version; a `-alpha.N` suffix means `prerelease/alpha` is the live line and the fix must land there, or uvs will never see it.

State on 2026-10-26: uvs `dev` consumes `1.10.2-alpha.1`. `prerelease/alpha` = `main` + the scenario-reset/evacuation work (restored on 2026-09-30 after the 2026-09-29 rollback on `main`). `main` is `1.10.1` without that work. A fix pushed to `main` would publish `1.10.2` on `latest`, which uvs cannot adopt without losing the reset work, and `main` → `alpha` merges conflict in `ScenarioManager.cs` because alpha rewrote it. So while uvs is on an alpha, fixes go to `prerelease/alpha`; `main` catches up when alpha is promoted (merge alpha into `main`, which publishes the stable version).

## Step by step

1. In `uvs-package-creator`, in `Assets/SceneManagement`: `git fetch`, branch off the live line (`git switch -c <topic> origin/prerelease/alpha`), make the change, run the package's EditMode tests in the uvs-package-creator Unity project (`Tests/Editor`).
2. Commit with a releasable subject, e.g. `fix(sceneloader): log every failed scene load`. Push the topic branch and open a PR against `prerelease/alpha`, or push to `prerelease/alpha` directly if that is how the team works that week; only the push to the release branch triggers CI.
3. Watch the Actions run on GitHub. It ends with the `chore(release): …` commit on the branch and the new version on npm. `npm view fr.jeanf.scenemanagement dist-tags` shows it (alpha versions under `alpha`, stable under `latest`).
4. In uvs, on a branch off `dev`: set the new version in `Packages/manifest.json` (`"fr.jeanf.scenemanagement": "1.10.2-alpha.2"`), let the editor resolve it (Package Manager refreshes the lock file), check `Packages/packages-lock.json` changed along, commit both files, MR into `dev`. The usual subject is `chore(packages): scenemanagement x.y.z — <what it brings>`.
5. Every checkout of uvs (`~/uvs`, `~/uvs-dev`, teammates) gets the new code when it pulls that `dev` commit; the package cache under `Library/PackageCache` is per checkout and rebuilt automatically.

## Things that cost time before

- Editing `Library/PackageCache/fr.jeanf.scenemanagement@…` in uvs changes nothing durable; the cache is regenerated from the registry.
- Nested packages (`Samples/`, `Documentation~`) are part of the tarball; `Documentation~` is not imported by Unity, which is why design notes live there.
- The uvs-package-creator checkout can sit on an old detached commit while npm is several versions ahead. Always `git fetch` and look at `origin/main` and `origin/prerelease/*` before branching.
- A change on `main` does not reach alpha by itself, and alpha does not reach `main`: someone merges, and `ScenarioManager.cs` is the file that conflicts.

## The sibling packages must match what uvs consumes

The uvs-package-creator project compiles every nested package together, so a package on its live line only builds when its siblings are on theirs. SceneManagement `prerelease/alpha` calls `SendTeleportTarget.TeleportRequested`, `PlayerEvents.TeleportRequested` and `TeleportUtility`, which exist only in UniversalPlayer `1.21.2-alpha.x` (`origin/prerelease/alpha` of `lejeanf/UniversalPlayer`). With `Assets/UniversalPlayer` left on an older commit the project fails with `CS0117 … does not contain a definition for 'TeleportRequested'` before any test runs. Before working on a package: `git -C Assets/<Sibling> fetch` and check each sibling out at the version named in `uvs/Packages/manifest.json`.

This coupling is not declared: SceneManagement's `Runtime` asmdef references `jeanf.universalplayer`, but `package.json` does not list `fr.jeanf.universal.player` under `dependencies`, so UPM cannot enforce the minimum version in uvs either. Adding it (at the alpha version uvs runs) would make the requirement visible in both places.

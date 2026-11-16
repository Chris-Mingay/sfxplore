# Releases

`VERSION` is the source of the application version. Stable releases use `major.minor.patch` (currently `0.2.0`). Use patch increments for fixes and minor increments for feature releases while the app is below 1.0. Saved-mix serialization and Godot engine versions are independent of this number.

## One-time GitHub setup

1. Merge the workflows into the repository's default branch. The manual publish workflow runs from that branch.
2. Create a GitHub Actions environment named **itch-production**. Add required reviewers if you want another approval before publishing; a reviewer is optional.
3. Set an environment variable **ITCH_TARGET** to your lower-case itch slug, for example `your-account/sfxplore`. The itch project must already exist.
4. Run `butler login` locally. Add the resulting Butler/wharf credential as the environment secret **BUTLER_API_KEY**. Do not commit it. See [Butler authentication](https://itch.io/docs/butler/login.html).

The build workflow needs permission to create draft releases. It requests `contents: write` only for that job. The publish workflow only needs to read GitHub releases and workflow runs; its itch credential is available only in the production environment.

## Prepare a release

1. Update `VERSION` and add a matching `## X.Y.Z` section to `CHANGELOG.md`. Keep unreleased changes under `## Unreleased` until ready.
2. Run `python scripts/version.py` to synchronize Godot project and export metadata. The .NET assembly and About dialog read the version automatically.
3. Run `python scripts/version.py --check`, `python -m unittest discover -s scripts/tests -v`, and the app's build/tests. Commit the version, changelog, and synchronized metadata with the intended changes.
4. Push the commit, then create and push a tag matching `VERSION`:

   ```sh
   git tag -a v0.2.0 -m "SFXplore 0.2.0"
   git push origin v0.2.0
   ```

Never move a release tag to a different commit. CI rejects a tag that does not match `VERSION` or missing/stale version metadata.

## Build and review

The existing desktop workflow builds Windows x64, Linux x64, Mac Intel, and Mac Apple Silicon. Each platform runs unit tests and Godot interaction smoke tests, exports the app, and smoke-tests the exported executable before archiving it.

Archives contain the complete package, `VERSION`, `CHANGELOG.md`, and `build-info.json` with its version, Git commit, and architecture. Tar archives preserve Linux/Mac executable permissions and bundle links through GitHub artifact storage.

When all four tagged builds succeed, CI creates a **draft GitHub release** with the archives, changelog notes, and `release-manifest.json` recording their SHA-256 checksums and originating workflow run. These release assets retain the packages independently of the temporary Actions artifacts (90-day retention).

Wait for the entire build workflow to succeed. Download and launch the package for your platform from the draft release, checking the About version and a representative library/mix/export. The project currently has no release signing or Apple notarization; check the platform packages on their corresponding hosts.

## Publish to itch.io

1. Open **Actions → Publish tested release to itch.io → Run workflow** on the default branch.
2. Enter the existing tag, for example `v0.2.0`.
3. If configured, approve the `itch-production` environment deployment.

The action downloads the draft/existing release's archives without rebuilding them. It checks the tag's commit, successful source build, all four checksums, and embedded build identities before making any upload. Butler pushes the complete packages with `--userversion X.Y.Z` to these permanent channels:

| Architecture | itch channel |
| --- | --- |
| Windows x64 | `windows` |
| Linux x64 | `linux` |
| Mac Intel | `mac-intel` |
| Mac Apple Silicon | `mac-arm64` |

Check the platform tags and download labels in itch's Edit page after the first upload. Reusing the channels updates their existing downloads. See [Butler uploads and versions](https://itch.io/docs/butler/pushing.html).

Uploads across channels are sequential, not atomic. If one fails, inspect the error and rerun the same tag; already-uploaded channels can be pushed again. Concurrent publish runs are serialized. The workflow leaves the GitHub release as a draft; publish it separately if you also want public GitHub downloads.

For a broken public release, ship a corrected patch under a new version and tag. Retained draft release archives provide the previous binaries for investigation. Do not move tags or silently replace the verified assets. If draft creation failed midway, remove the incomplete draft before rerunning the tagged build. Missing/expired assets require a new verified build; the publisher does not rebuild implicitly.

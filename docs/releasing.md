# Releasing

One version number, one tag, one GitHub Release, and a publish that a person starts.

## 1. Set the version

```console
bash scripts/version.sh --set X.Y.Z
```

That writes `X.Y.Z` into `rust/Cargo.toml` and the package project, and lets cargo write `rust/Cargo.lock`.
It refuses to rewrite those files while any of them is dirty, and it neither commits nor tags: commit the
three.

## 2. Tag and push

```console
git tag vX.Y.Z
git push origin vX.Y.Z
```

A tag push starts `.github/workflows/release.yml`, which runs the checks and builds all five RIDs, packs and
attests the package and creates the GitHub Release with it attached. Nothing in that workflow publishes to
NuGet. Starting it by hand builds and checks the same package without attesting or releasing it.

## 3. Configure trusted publishing, once

nuget.org issues a short-lived key per run, so no API key is stored in this repository.

1. On nuget.org, add a trusted publishing policy: Repository Owner `vinhch`, Repository
   `rust-trafilatura-dotnet`, Workflow File `publish.yml`, Environment `nuget`. The workflow file is the
   file name only, without the `.github/workflows/` path.
2. In the repository, create the environment `nuget`, with a required reviewer if publishing should need an
   approval on top of being started by hand.
3. Add a secret `NUGET_USER` holding the nuget.org profile name.

## 4. Publish

Actions, `publish`, Run workflow, with the released tag (`v1.2.3` or `1.2.3`), or leave it empty to publish
the most recent release. The run checks the tag against the sources at that tag, downloads that release's
package, verifies the attestation tying it to the tag, obtains a short-lived key and pushes.

Run it with `dry_run` set first: that is every step except the push, so it is what proves the policy above
matches. A version NuGet already has is skipped rather than reported as an error, so re-running is safe.

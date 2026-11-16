# Package smoke test

A small sample plugin, laid out like a plugin repository (`backend/`, `frontend/`, `tests/`), that builds and tests
against the packages core publishes and nothing of core's source (docs/architecture.md §9). CI runs it on every
pull request; run it yourself with:

```bash
npm ci                          # at the repository root: the frontend SDK is built from there
testing/package-smoke/smoke.sh
```

`smoke.sh` packs `NKeeper.Abstractions`, `NKeeper.Plugin.Build`, `NKeeper.Testing` and the frontend SDK into `feed/`, at a
throwaway version, then builds the sample and runs its tests, restoring NuGet packages only from `feed/` and nuget.org
(`NuGet.config`) and the frontend SDK from its tarball. The root `Directory.Build.props` and `Directory.Packages.props`
are shadowed here on purpose, so nothing is inherited from core's solution.

The sample's `frontend/package.json` and `backend/NKeeper.Smoke.csproj` show how a plugin repository consumes the
packages; release builds there use `1.0.0` or later in place of the smoke version.

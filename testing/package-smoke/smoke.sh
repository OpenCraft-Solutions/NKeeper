#!/usr/bin/env bash
# Builds and tests the sample plugin against freshly packed packages only (see README.md).
# Usage: testing/package-smoke/smoke.sh [version]    (run `npm ci` at the repository root first)
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
version=${1:-1.0.0-smoke.$(date +%s)}

# A clean slate: nothing left over from an earlier run may satisfy a restore.
rm -rf "$here/feed" "$here/artifacts" "$here/.dev" "$here/frontend/node_modules" "$here/frontend/dist"
mkdir -p "$here/feed"

for project in core/backend/NKeeper.Abstractions core/packaging/NKeeper.Plugin.Build testing/NKeeper.Testing; do
  dotnet pack "$root/$project" --configuration Release --output "$here/feed" -p:NKeeperPackageVersion="$version"
done

npm run build --workspace @nkeeper/sdk --prefix "$root"
npm pack "$root/core/frontend/sdk/dist" --pack-destination "$here/feed"

# The sample asks for the SDK as a plugin repository does (npm:@opencraft-solutions/nkeeper-sdk@^1.0.0), which only
# GitHub Packages serves; here the tarball just packed stands in for it, under the same import name.
cd "$here/frontend"
npm install --no-package-lock --no-save "@nkeeper/sdk@file:../feed/$(cd ../feed && ls opencraft-solutions-nkeeper-sdk-*.tgz)"
npm test

cd "$here"
dotnet test tests/NKeeper.Smoke.Tests --configuration Release -p:NKeeperSmokeVersion="$version"

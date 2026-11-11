#!/usr/bin/env bash
# Checks a native install from the outside, through nginx (docs/architecture.md §8): the shell is served,
# the first administrator can sign in, and every built-in plugin loaded.
#
#   deploy/check-install.sh <administrator password> [base URL, default https://localhost] [user, default admin]
set -euo pipefail

password=${1:?usage: deploy/check-install.sh <administrator password> [base URL] [user]}
base=${2:-https://localhost}
user=${3:-admin}
jar=$(mktemp)
trap 'rm -f "$jar"' EXIT

# -k: the installer's certificate is self-signed unless one was given.
get() { curl -fsSk -c "$jar" -b "$jar" "$@"; }

get "$base/" | grep -q '<div id="root">' || { echo "The shell isn't served at $base/." >&2; exit 1; }

antiforgery=$(get "$base/api/auth/antiforgery")
token=$(sed -n 's/.*"token":"\([^"]*\)".*/\1/p' <<<"$antiforgery")
header=$(sed -n 's/.*"headerName":"\([^"]*\)".*/\1/p' <<<"$antiforgery")
[ -n "$token" ] && [ -n "$header" ] || { echo "No antiforgery token: $antiforgery" >&2; exit 1; }

get -X POST -H "$header: $token" -H 'Content-Type: application/json' \
    -d "{\"userNameOrEmail\":\"$user\",\"password\":\"$password\",\"rememberMe\":false}" \
    "$base/api/auth/login" >/dev/null || { echo "Signing in as $user failed." >&2; exit 1; }

plugins=$(get "$base/api/plugins")
echo "Plugins: $plugins"
# The installer installs only the built-in plugins, so each one must have loaded.
states=$(grep -o '"state":"[A-Za-z]*"' <<<"$plugins" || true)
if [ -z "$states" ] || grep -qv '"state":"loaded"' <<<"$states"; then
    echo "No plugin is installed, or one didn't load." >&2
    exit 1
fi
echo "The install works: the shell is served, $user signs in, and the built-in plugins loaded."

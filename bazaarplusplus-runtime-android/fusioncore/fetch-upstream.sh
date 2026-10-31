#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROPERTIES="$ROOT/fusioncore/upstream.properties"
CACHE="$ROOT/build/fusioncore"
SOURCE="$CACHE/source"

property() {
    sed -n "s/^$1=//p" "$PROPERTIES"
}

repository="$(property repository)"
commit="$(property commit)"
archive="$CACHE/FusionCore-$commit.tar.gz"
marker="$SOURCE/.bpp-upstream-commit"

if [[ -f "$marker" ]] && [[ "$(tr -d '\r\n' < "$marker")" == "$commit" ]]; then
    printf 'FusionCore source ready: %s\n' "$SOURCE"
    exit 0
fi

mkdir -p "$CACHE"
temporary="$(mktemp -d "$CACHE/fetch.XXXXXX")"
trap 'rm -rf "$temporary"' EXIT

curl --fail --location --retry 3 --connect-timeout 20 \
    "$repository/archive/$commit.tar.gz" \
    --output "$archive"
tar -xzf "$archive" -C "$temporary" --strip-components=1
printf '%s\n' "$commit" > "$temporary/.bpp-upstream-commit"

if [[ -e "$SOURCE" ]]; then
    previous="$CACHE/source.previous"
    rm -rf "$previous"
    mv "$SOURCE" "$previous"
fi
mv "$temporary" "$SOURCE"
trap - EXIT
printf 'FusionCore source ready: %s\n' "$SOURCE"


#!/usr/bin/env bash
#
# bump-version.sh <major|minor|patch> [path-to-project.csproj]
#
# C# / .NET projects. Bumps the first <Version> element inside the first
# <PropertyGroup> - never a <PackageReference Version="..."> attribute.
#
set -euo pipefail

BUMP_TYPE="${1:-}"
MANIFEST="${2:-}"

case "$BUMP_TYPE" in
  major | minor | patch) ;;
  *)
    echo "Usage: $0 <major|minor|patch> [project.csproj]" >&2
    exit 1
    ;;
esac

if [ -z "$MANIFEST" ]; then
  MANIFEST="$(find . -maxdepth 2 -name '*.csproj' | head -n 1)"
fi

if [ -z "$MANIFEST" ] || [ ! -f "$MANIFEST" ]; then
  echo "Not found: ${MANIFEST:-*.csproj}" >&2
  exit 1
fi

CURRENT="$(awk '
  match($0, /<Version>[^<]*<\/Version>/) {
    field = substr($0, RSTART + 9, RLENGTH - 19)
    print field
    exit
  }
' "$MANIFEST")"

if [ -z "$CURRENT" ]; then
  echo "No <Version> element in $MANIFEST" >&2
  echo "Add one under <PropertyGroup> - <VersionPrefix> and Directory.Build.props are not read here" >&2
  exit 1
fi

CORE="${CURRENT%%-*}"
CORE="${CORE%%+*}"

IFS='.' read -r MAJOR MINOR PATCH <<<"$CORE"

if ! [[ "$MAJOR" =~ ^[0-9]+$ && "$MINOR" =~ ^[0-9]+$ && "$PATCH" =~ ^[0-9]+$ ]]; then
  echo "Version '$CURRENT' is not semver (x.y.z)" >&2
  exit 1
fi

case "$BUMP_TYPE" in
  major)
    MAJOR=$((MAJOR + 1))
    MINOR=0
    PATCH=0
    ;;
  minor)
    MINOR=$((MINOR + 1))
    PATCH=0
    ;;
  patch)
    PATCH=$((PATCH + 1))
    ;;
esac

NEW="${MAJOR}.${MINOR}.${PATCH}"

if awk -v new="$NEW" '
  !done && match($0, /<Version>[^<]*<\/Version>/) {
    head = substr($0, 1, RSTART - 1)
    tail = substr($0, RSTART + RLENGTH)
    print head "<Version>" new "</Version>" tail
    done = 1
    next
  }
  { print }
  END { exit !done }
' "$MANIFEST" >"$MANIFEST.tmp"; then
  mv "$MANIFEST.tmp" "$MANIFEST"
else
  rm -f "$MANIFEST.tmp"
  echo "Failed to rewrite <Version> in $MANIFEST" >&2
  exit 1
fi

echo "$CURRENT -> $NEW ($BUMP_TYPE)" >&2
echo "$NEW"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "version=$NEW"
    echo "previous_version=$CURRENT"
  } >>"$GITHUB_OUTPUT"
fi

#!/bin/bash
# Rename the installer or AppImage to the per-OS download name.
# Leave *-full.nupkg and releases.*.json as vpk wrote them.
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: rename-release-assets.sh <output-dir> <version> <windows|macos|macos-intel|linux> <channel>" >&2
  exit 1
fi

output=$1
version=$2
os=$3
channel=$4

if [ -z "$version" ] || [ -z "$channel" ] || [ ! -d "$output" ]; then
  echo "output directory, version, and channel are required" >&2
  exit 1
fi

reject() {
  pattern=$1
  label=$2
  found=0
  for f in "$output"/$pattern; do
    if [ -e "$f" ]; then
      echo "$label was produced and is not uploaded: $(basename "$f")" >&2
      found=1
    fi
  done
  if [ "$found" -ne 0 ]; then
    exit 1
  fi
}

one() {
  pattern=$1
  label=$2
  match=""
  count=0
  for f in "$output"/$pattern; do
    if [ -e "$f" ]; then
      match=$f
      count=$((count + 1))
    fi
  done
  if [ "$count" -ne 1 ]; then
    echo "expected one $label in $output, found $count" >&2
    exit 1
  fi
  printf '%s\n' "$match"
}

reject '*-delta.nupkg' 'delta package'
reject '*Portable.zip' 'portable zip'

full=$(one '*-full.nupkg' 'full package')
releases=$(one 'releases.*.json' 'releases index')
assets=$(one 'assets.*.json' 'assets index')
full_name=$(basename "$full")
releases_name=$(basename "$releases")

expected_full="GitStronghold-${version}-${channel}-full.nupkg"
expected_releases="releases.${channel}.json"
if [ "$full_name" != "$expected_full" ]; then
  echo "full package is $full_name; the updater expects $expected_full" >&2
  exit 1
fi
if [ "$releases_name" != "$expected_releases" ]; then
  echo "releases index is $releases_name; the updater expects $expected_releases" >&2
  exit 1
fi

case "$os" in
  windows)
    src=$(one '*Setup.exe' 'setup exe')
    new_name="GitStronghold-${version}-Windows-Setup.exe"
    ;;
  macos)
    src=$(one '*Setup.pkg' 'setup pkg')
    new_name="GitStronghold-${version}-macOS-AppleSilicon-Setup.pkg"
    ;;
  macos-intel)
    src=$(one '*Setup.pkg' 'setup pkg')
    new_name="GitStronghold-${version}-macOS-Intel-Setup.pkg"
    ;;
  linux)
    src=$(one '*.AppImage' 'AppImage')
    new_name="GitStronghold-${version}-Linux-x64.AppImage"
    ;;
  *)
    echo "unknown os: $os" >&2
    exit 1
    ;;
esac

old_name=$(basename "$src")
if [ "$old_name" = "$new_name" ]; then
  echo "download is already named $new_name" >&2
  exit 1
fi

mv "$src" "$output/$new_name"

tmp="$assets.tmp"
: > "$tmp"
while IFS= read -r line || [ -n "$line" ]; do
  printf '%s\n' "${line//$old_name/$new_name}"
done < "$assets" > "$tmp"
mv "$tmp" "$assets"

if ! grep -F -q "$new_name" "$assets"; then
  echo "$new_name was not written into $(basename "$assets")" >&2
  exit 1
fi
if grep -F -q "$old_name" "$assets"; then
  echo "$old_name is still listed in $(basename "$assets")" >&2
  exit 1
fi

echo "Renamed $old_name -> $new_name"
echo "Kept $full_name"
echo "Kept $releases_name"

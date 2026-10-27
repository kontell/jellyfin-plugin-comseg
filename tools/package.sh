#!/usr/bin/env bash
# Build the Jellyfin-installable zip: the artifact named in build.yaml,
# the logo, plus the meta.json the server reads at load time. Output: dist/.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$repo/dist}"
proj="$repo/Jellyfin.Plugin.Comseg/Jellyfin.Plugin.Comseg.csproj"
stage="$repo/artifacts/publish"

# The ABI row being packaged. Unset means the primary row, so a bare run of
# this script is exactly what it always was; the build matrix sets all four to
# build the same source against another Jellyfin line.
#
#   ABI_BASE          version prefix, e.g. 12.0.0    (default: from PluginVersion)
#   TARGET_ABI        minimum server, e.g. 12.0.0.0  (default: from build.yaml)
#   FRAMEWORK         e.g. net10.0                   (default: the csproj's)
#   JELLYFIN_VERSION  package pin, e.g. 12.0.0-rc5   (default: the csproj's)
plugin_version="$(sed -n 's/.*<PluginVersion[^>]*>\(.*\)<\/PluginVersion>.*/\1/p' "$proj")"
build_number="${plugin_version##*.}"

if [ -n "${ABI_BASE:-}" ]; then
    # One build number across every row: <abi base>.<build>.
    version="$ABI_BASE.$build_number"
else
    version="$plugin_version"
fi
guid="$(sed -n 's/^guid: *"\(.*\)"/\1/p' "$repo/build.yaml")"
name="$(sed -n 's/^name: *"\(.*\)"/\1/p' "$repo/build.yaml")"
target_abi="${TARGET_ABI:-$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' "$repo/build.yaml")}"
owner="$(sed -n 's/^owner: *"\(.*\)"/\1/p' "$repo/build.yaml")"
overview="$(sed -n 's/^overview: *"\(.*\)"/\1/p' "$repo/build.yaml")"
description="$(sed -n 's/^description: *"\(.*\)"/\1/p' "$repo/build.yaml")"
image="$(sed -n 's/^image: *"\(.*\)"/\1/p' "$repo/build.yaml")"
image_url="$(sed -n 's/^imageUrl: *"\(.*\)"/\1/p' "$repo/build.yaml")"

# The changelog block, flattened to one JSON string. repository.kontell's
# generate_jellyfin_repo.py already reads meta.json's "changelog" and has been
# getting "" for it, which is what an empty release note in the Jellyfin plugin
# catalogue was: a field nobody wrote, not a field nobody wanted.

changelog="$(awk '
    /^changelog:[[:space:]]*\|-?[[:space:]]*$/ { flag = 1; next }
    flag && /^[^[:space:]]/ { exit }
    flag { sub(/^  /, ""); print }
' "$repo/build.yaml" \
    | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' \
    | awk 'NR > 1 { printf "\\n" } { printf "%s", $0 }')"

build_args=(-c Release -o "$stage" -p:PluginVersion="$version")
[ -n "${FRAMEWORK:-}" ] && build_args+=(-p:JellyfinFramework="$FRAMEWORK")
[ -n "${JELLYFIN_VERSION:-}" ] && build_args+=(-p:JellyfinVersion="$JELLYFIN_VERSION")

echo "packaging $name $version (targetAbi $target_abi${FRAMEWORK:+, $FRAMEWORK})"
dotnet publish "$proj" "${build_args[@]}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp "$stage/Jellyfin.Plugin.Comseg.dll" "$work/"

# The logo travels inside the zip, because Jellyfin serves the installed
# plugin's image off disk from the plugin's own folder. Fail rather than ship a
# meta.json whose imagePath points at nothing: the server answers that with a
# silent 404 and a blank tile, which looks exactly like having no logo at all.
if [ -n "$image" ]; then
    [ -f "$repo/$image" ] || { echo "build.yaml names image $image, which does not exist" >&2; exit 1; }
    cp "$repo/$image" "$work/"
fi

cat > "$work/meta.json" <<EOF
{
    "category": "General",
    "guid": "$guid",
    "name": "$name",
    "overview": "$overview",
    "description": "$description",
    "owner": "$owner",
    "imagePath": "$image",
    "imageUrl": "$image_url",
    "targetAbi": "$target_abi",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$version",
    "changelog": "$changelog",
    "status": "Active",
    "autoUpdate": false
}
EOF

# Absolute, because the zip below runs from "$work": a relative "$out" (CI
# passes "dist") would resolve against the temp dir, not the repo.
mkdir -p "$out"
out="$(cd "$out" && pwd)"
zip_path="$out/comseg_$version.zip"
rm -f "$zip_path"
(cd "$work" && zip -q -r "$zip_path" .)

echo "$zip_path"
unzip -l "$zip_path"

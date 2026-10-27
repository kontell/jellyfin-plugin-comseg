#!/usr/bin/env bash
# Build the patched Comskip the plugin's live detection needs.
#
#   tools/build-comskip.sh [--src <Comskip checkout>] [--prefix <install dir>]
#
# Clones erikkaashoek/Comskip (master) unless --src names a checkout, applies
# comskip/comskip-live-master.patch, builds without the SDL GUI and installs
# the binary under <prefix>/bin (default: ./comskip/build/prefix). Point the
# plugin's "Comskip executable" setting at that binary.
#
# Debian/Ubuntu build deps: build-essential autoconf automake libtool
# pkg-config libargtable2-dev libavformat-dev libavcodec-dev libavutil-dev
# libswscale-dev. FFmpeg 6 or 7; FFmpeg 8 needs upstream's pending fix.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src=""
prefix="$repo/comskip/build/prefix"
while [ $# -gt 0 ]; do
    case "$1" in
        --src) src="$2"; shift 2 ;;
        --prefix) prefix="$2"; shift 2 ;;
        *) echo "unknown option $1" >&2; exit 2 ;;
    esac
done

work="$repo/comskip/build"
mkdir -p "$work"
if [ -z "$src" ]; then
    src="$work/src"
    if [ ! -d "$src/.git" ]; then
        git clone --depth 1 https://github.com/erikkaashoek/Comskip "$src"
    fi
fi

patched="$work/patched"
rm -rf "$patched"
rsync -a --exclude .git "$src/" "$patched/"
patch -d "$patched" -p1 < "$repo/comskip/comskip-live-master.patch"

cd "$patched"
./autogen.sh
mkdir -p "$work/obj"
cd "$work/obj"
"$patched/configure" --disable-gui --prefix="$prefix"
make -j"$(nproc)"
make install
echo "built: $prefix/bin/comskip"
"$prefix/bin/comskip" --help 2>&1 | head -1 || true

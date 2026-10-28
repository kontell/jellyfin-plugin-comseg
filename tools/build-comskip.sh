#!/usr/bin/env bash
# Build the Comskip the plugin's live detection needs.
#
#   tools/build-comskip.sh [--src <Comskip checkout>] [--prefix <install dir>]
#
# Clones the comseg branch of https://github.com/kontell/Comskip (upstream
# Comskip plus the periodic live analysis, see comskip/README.md) unless --src
# names a checkout, builds without the SDL GUI and installs the binary under
# <prefix>/bin (default: ./comskip/build/prefix). Point the plugin's "Comskip
# executable" setting at that binary.
#
# Debian/Ubuntu build deps: build-essential autoconf automake libtool
# pkg-config libargtable2-dev libavformat-dev libavcodec-dev libavutil-dev
# libswscale-dev. FFmpeg 6 or 7; FFmpeg 8 needs upstream's pending fix. With
# no system argtable2, export argtable2_CFLAGS and argtable2_LIBS for configure.
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
        git clone --depth 1 --branch comseg https://github.com/kontell/Comskip "$src"
    fi
fi

# autogen writes into the tree, so build a copy and leave the checkout clean.
tree="$work/tree"
rm -rf "$tree"
rsync -a --exclude .git "$src/" "$tree/"

cd "$tree"
./autogen.sh
mkdir -p "$work/obj"
cd "$work/obj"
"$tree/configure" --disable-gui --prefix="$prefix"
make -j"${JOBS:-$(nproc)}"
make install
echo "built: $prefix/bin/comskip"
"$prefix/bin/comskip" --help 2>&1 | head -1 || true

#!/bin/bash
# Cross-compile rmlui_c.dll (RmlUi + FreeType + the C shim, static CRT) for Windows x64 from Linux.
# Needs clang-cl, lld-link, llvm-lib, llvm-rc, llvm-mt, cmake, ninja, an xwin splat (see xwin-clang-cl.cmake),
# RmlUi at external/RmlUi and FreeType at external/freetype.
#
# Usage: cross/build-windows.sh [backend]
#   backend: one of RmlUi's Win32 desktop backends (e.g. Win32_DX11) to include, for a standalone window.
#            Omit for the in-game build.
#
# CMAKE_*_FLAGS_INIT only seeds a fresh cache, so both build directories are recreated every run.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKEND="${1:-}"
BUILD="$REPO/native/build/win${BACKEND:+-$BACKEND}"
TOOLCHAIN="$REPO/cross/xwin-clang-cl.cmake"
FT_BUILD="$BUILD/freetype"
FT_PREFIX="$BUILD/freetype-install"

[ -f "$REPO/external/freetype/CMakeLists.txt" ] || { echo "no FreeType at external/freetype" >&2; exit 1; }

rm -rf "$BUILD"

echo "==> FreeType (static; no zlib, bzip2, png, brotli or harfbuzz)"
cmake -S "$REPO/external/freetype" -B "$FT_BUILD" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$FT_PREFIX" \
    -DBUILD_SHARED_LIBS=OFF \
    -DFT_DISABLE_ZLIB=ON -DFT_DISABLE_BZIP2=ON -DFT_DISABLE_PNG=ON -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON
cmake --build "$FT_BUILD" --target install

echo "==> rmlui_c.dll"
cmake -S "$REPO/native" -B "$BUILD" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" -DCMAKE_BUILD_TYPE=Release \
    -DRMLC_SHARED=ON -DRMLC_BACKEND="$BACKEND" \
    -DFREETYPE_LIBRARY="$FT_PREFIX/lib/freetype.lib" \
    -DFREETYPE_INCLUDE_DIR_ft2build="$FT_PREFIX/include/freetype2" \
    -DFREETYPE_INCLUDE_DIR_freetype2="$FT_PREFIX/include/freetype2"
cmake --build "$BUILD" --target rmlui_c

echo
echo "built: $BUILD/rmlui_c.dll"

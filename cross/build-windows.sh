#!/bin/bash
# Cross-compile rmlui_c.dll (RmlUi + FreeType + the C shim, static CRT) for Windows x64 from Linux.
# Needs clang-cl, lld-link, llvm-lib, llvm-rc, llvm-mt, cmake, ninja, an xwin splat (see xwin-clang-cl.cmake),
# RmlUi at external/RmlUi and FreeType at external/freetype; the desktop build also SDL3 at external/SDL and stb at
# external/stb.
#
# Usage: cross/build-windows.sh [desktop] [extra cmake arguments...]
#   desktop: include the standalone window backend (SDL3; Vulkan, else OpenGL 3) -> native/build/win-desktop.
#            Omit for the in-game build -> native/build/win, which has no SDL in it.
#
# CMAKE_*_FLAGS_INIT only seeds a fresh cache, so both build directories are recreated every run.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESKTOP=OFF
if [ "${1:-}" = "desktop" ]; then
    DESKTOP=ON
    shift
fi
BUILD="$REPO/native/build/win"
if [ "$DESKTOP" = ON ]; then
    BUILD="$REPO/native/build/win-desktop"
fi
TOOLCHAIN="$REPO/cross/xwin-clang-cl.cmake"
FT_BUILD="$BUILD/freetype"
FT_PREFIX="$BUILD/freetype-install"

[ -f "$REPO/external/freetype/CMakeLists.txt" ] || { echo "no FreeType at external/freetype" >&2; exit 1; }
if [ "$DESKTOP" = ON ]; then
    [ -f "$REPO/external/SDL/CMakeLists.txt" ] || { echo "no SDL3 at external/SDL" >&2; exit 1; }
    [ -f "$REPO/external/stb/stb_image.h" ] || { echo "no stb at external/stb" >&2; exit 1; }
fi

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
    -DRMLC_SHARED=ON -DRMLC_DESKTOP="$DESKTOP" \
    -DFREETYPE_LIBRARY="$FT_PREFIX/lib/freetype.lib" \
    -DFREETYPE_INCLUDE_DIR_ft2build="$FT_PREFIX/include/freetype2" \
    -DFREETYPE_INCLUDE_DIR_freetype2="$FT_PREFIX/include/freetype2" \
    "$@"
cmake --build "$BUILD" --target rmlui_c

echo
echo "built: $BUILD/rmlui_c.dll"

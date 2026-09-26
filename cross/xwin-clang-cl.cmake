# Cross-compile to x86_64 Windows (MSVC ABI) from Linux: clang-cl + lld-link against an xwin splat.
# Adapted from Donkey-Kong-64-UnKONGtrolled's xwin-clang-cl.cmake. Differences: the C and C++ runtimes are linked
# statically (players need no VC++ redistributable, which matters under Proton), and DirectX-Headers is optional.
#
# Prerequisites: an xwin splat at $ENV{XWIN_SPLAT}, ~/.xwin-cache/splat or ~/.cache/xwin/splat
# (`xwin --accept-license splat`, ~2.4 GB, one time).

set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

if(DEFINED ENV{XWIN_SPLAT})
    set(XWIN_SPLAT $ENV{XWIN_SPLAT})
else()
    set(XWIN_SPLAT "$ENV{HOME}/.xwin-cache/splat")
    foreach(_c "$ENV{HOME}/.xwin-cache/splat" "$ENV{HOME}/.cache/xwin/splat")
        if(IS_DIRECTORY "${_c}/crt/include")
            set(XWIN_SPLAT "${_c}")
            break()
        endif()
    endforeach()
endif()
if(NOT EXISTS "${XWIN_SPLAT}/crt/include")
    message(FATAL_ERROR "No xwin splat at ${XWIN_SPLAT}; set XWIN_SPLAT or run 'xwin --accept-license splat'")
endif()

set(CMAKE_C_COMPILER clang-cl)
set(CMAKE_CXX_COMPILER clang-cl)
set(CMAKE_LINKER lld-link)
set(CMAKE_AR llvm-lib)
set(CMAKE_RC_COMPILER llvm-rc)
set(CMAKE_MT llvm-mt)
set(CMAKE_C_COMPILER_TARGET x86_64-pc-windows-msvc)
set(CMAKE_CXX_COMPILER_TARGET x86_64-pc-windows-msvc)

set(_XWIN_INCLUDES
    "/imsvc${XWIN_SPLAT}/crt/include"
    "/imsvc${XWIN_SPLAT}/sdk/include/ucrt"
    "/imsvc${XWIN_SPLAT}/sdk/include/um"
    "/imsvc${XWIN_SPLAT}/sdk/include/shared")

# DirectX-Headers, if given, shadows the SDK's older d3d12.h. Not needed for DX11.
set(_DX_INCLUDES "")
if(DEFINED ENV{DIRECTX_HEADERS} AND EXISTS "$ENV{DIRECTX_HEADERS}/directx/d3d12.h")
    set(_DX_INCLUDES "/I$ENV{DIRECTX_HEADERS}/directx")
endif()

string(JOIN " " _XWIN_CFLAGS ${_DX_INCLUDES} ${_XWIN_INCLUDES})

# Static runtime (/MT). xwin splats only the release CRT, so every configuration uses it.
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
list(APPEND CMAKE_TRY_COMPILE_PLATFORM_VARIABLES CMAKE_MSVC_RUNTIME_LIBRARY)
set(CMAKE_C_FLAGS_RELEASE_INIT "/O2 /Ob2 /DNDEBUG")
set(CMAKE_CXX_FLAGS_RELEASE_INIT "/O2 /Ob2 /DNDEBUG")
set(CMAKE_C_FLAGS_INIT "${_XWIN_CFLAGS}")
set(CMAKE_CXX_FLAGS_INIT "${_XWIN_CFLAGS} /EHsc")
# llvm-rc does not see the compiler's /imsvc paths; resource scripts include windows.h too.
string(JOIN " " CMAKE_RC_FLAGS_INIT
    "/I${XWIN_SPLAT}/sdk/include/um" "/I${XWIN_SPLAT}/sdk/include/shared"
    "/I${XWIN_SPLAT}/sdk/include/ucrt" "/I${XWIN_SPLAT}/crt/include")

# xwin names these x86_64 by default and x64 with --preserve-ms-arch-notation.
function(_xwin_libdir out base)
    foreach(_a x64 x86_64)
        if(IS_DIRECTORY "${base}/${_a}")
            set(${out} "${base}/${_a}" PARENT_SCOPE)
            return()
        endif()
    endforeach()
    message(FATAL_ERROR "No x64 or x86_64 library directory under ${base}")
endfunction()
_xwin_libdir(_CRT_LIB "${XWIN_SPLAT}/crt/lib")
_xwin_libdir(_UM_LIB "${XWIN_SPLAT}/sdk/lib/um")
_xwin_libdir(_UCRT_LIB "${XWIN_SPLAT}/sdk/lib/ucrt")
if(NOT EXISTS "${_UM_LIB}/kernel32.lib")
    message(FATAL_ERROR "${_UM_LIB} has no lowercase kernel32.lib; re-run 'xwin splat' with symlinks enabled")
endif()

string(JOIN " " _XWIN_LDFLAGS "/libpath:${_CRT_LIB}" "/libpath:${_UM_LIB}" "/libpath:${_UCRT_LIB}")
set(CMAKE_EXE_LINKER_FLAGS_INIT "${_XWIN_LDFLAGS}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_XWIN_LDFLAGS}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_XWIN_LDFLAGS}")

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)

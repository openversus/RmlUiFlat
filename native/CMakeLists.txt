cmake_minimum_required(VERSION 3.16)
project(rmlui_c LANGUAGES C CXX)

# RMLC_BACKEND: one of RmlUi's desktop backends (X11_GL2, Win32_DX11, ...) to build as rmlui_c_backend, for a
# standalone window (demo, installer). Empty builds only the bindings; in-game rendering does not use these backends.
set(RMLC_BACKEND "" CACHE STRING "RmlUi desktop backend to wrap, or empty for none")

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(RMLUI_SAMPLES OFF CACHE BOOL "" FORCE)
set(RMLUI_PRECOMPILED_HEADERS OFF CACHE BOOL "" FORCE)
if(RMLC_BACKEND)
    # RmlUi only adds its Backends directory for the shell; this adds it without the samples.
    set(RMLUI_SHELL ON)
    set(RMLUI_BACKEND "${RMLC_BACKEND}" CACHE STRING "" FORCE)
endif()
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_subdirectory(../external/RmlUi RmlUi EXCLUDE_FROM_ALL)

# RMLC_SHARED builds rmlui_c as a DLL/shared object with RmlUi, FreeType and (with RMLC_BACKEND) the desktop backend
# inside it, for a client that loads it at runtime. Otherwise it is a static library for linking into an executable.
option(RMLC_SHARED "Build rmlui_c as a shared library" OFF)

if(RMLC_SHARED)
    add_library(rmlui_c SHARED
        generated/rmlui_c.cpp
        rmlui_c_manual.cpp
    )
    if(RMLC_BACKEND)
        target_sources(rmlui_c PRIVATE backend/rmlui_c_backend.cpp)
        target_link_libraries(rmlui_c PRIVATE rmlui_backend_${RMLC_BACKEND})
    endif()
else()
    add_library(rmlui_c STATIC
        generated/rmlui_c.cpp
        rmlui_c_manual.cpp
    )
    target_compile_definitions(rmlui_c PUBLIC RMLC_STATIC)
endif()
target_compile_features(rmlui_c PRIVATE cxx_std_20)
target_include_directories(rmlui_c PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

# The repository's one version number (VERSION, also read by Directory.Build.props) as the DLL's version resource.
# Each part goes through math() so 2026.09.26.01 becomes 2026,9,26,1: the resource compiler reads 09 as octal.
if(WIN32 AND RMLC_SHARED)
    file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION" RMLC_VERSION)
    string(STRIP "${RMLC_VERSION}" RMLC_VERSION)
    string(REPLACE "." ";" _parts "${RMLC_VERSION}")
    list(LENGTH _parts _count)
    if(NOT _count EQUAL 4)
        message(FATAL_ERROR "VERSION must have four dot-separated numbers, got '${RMLC_VERSION}'")
    endif()
    set(_numbers "")
    foreach(_p IN LISTS _parts)
        math(EXPR _n "${_p}")
        list(APPEND _numbers "${_n}")
    endforeach()
    string(REPLACE ";" "," RMLC_VERSION_COMMAS "${_numbers}")
    configure_file(rmlui_c.rc.in "${CMAKE_CURRENT_BINARY_DIR}/rmlui_c.rc" @ONLY)
    target_sources(rmlui_c PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/rmlui_c.rc")
endif()
target_link_libraries(rmlui_c PUBLIC RmlUi::Core)

if(RMLC_BACKEND AND NOT RMLC_SHARED)
    add_library(rmlui_c_backend STATIC backend/rmlui_c_backend.cpp)
    target_compile_features(rmlui_c_backend PRIVATE cxx_std_20)
    target_link_libraries(rmlui_c_backend PUBLIC rmlui_c rmlui_backend_${RMLC_BACKEND})
endif()

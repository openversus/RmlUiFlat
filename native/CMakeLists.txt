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

add_library(rmlui_c STATIC
    generated/rmlui_c.cpp
    rmlui_c_manual.cpp
)
target_compile_features(rmlui_c PRIVATE cxx_std_20)
target_compile_definitions(rmlui_c PUBLIC RMLC_STATIC)
target_include_directories(rmlui_c PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(rmlui_c PUBLIC RmlUi::Core)

if(RMLC_BACKEND)
    add_library(rmlui_c_backend STATIC backend/rmlui_c_backend.cpp)
    target_compile_features(rmlui_c_backend PRIVATE cxx_std_20)
    target_link_libraries(rmlui_c_backend PUBLIC rmlui_c rmlui_backend_${RMLC_BACKEND})
endif()

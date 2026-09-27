cmake_minimum_required(VERSION 3.16)
project(rmlui_c LANGUAGES C CXX)

# RMLC_DESKTOP adds a standalone window (backend/rmlui_c_backend.h): SDL3 for the window and input, RmlUi's Vulkan
# renderer when the machine can run it and its OpenGL 3 renderer otherwise. For the installer, tools and the demo; the
# in-game build leaves it off and has no SDL in it.
option(RMLC_DESKTOP "Build the standalone window backend (SDL3; Vulkan, else OpenGL 3)" OFF)
option(RMLC_DESKTOP_VULKAN "Include RmlUi's Vulkan renderer in the desktop backend" ON)

# RMLC_OVERLAY adds the in-game overlay (overlay/rmlui_c_overlay.h): RmlUi's DX11 renderer drawing with a game's device
# into its swap chain, and its Win32 system interface. Windows only; the in-game build turns it on.
option(RMLC_OVERLAY "Build the in-game overlay backend (Direct3D 11; Windows)" OFF)

set(RMLUI_BACKENDS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../external/RmlUi/Backends")

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(RMLUI_SAMPLES OFF CACHE BOOL "" FORCE)
set(RMLUI_PRECOMPILED_HEADERS OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_subdirectory(../external/RmlUi RmlUi EXCLUDE_FROM_ALL)

if(RMLC_DESKTOP)
    set(SDL_SHARED OFF CACHE BOOL "" FORCE)
    set(SDL_STATIC ON CACHE BOOL "" FORCE)
    set(SDL_TEST_LIBRARY OFF CACHE BOOL "" FORCE)
    set(SDL_TESTS OFF CACHE BOOL "" FORCE)
    set(SDL_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(SDL_INSTALL OFF CACHE BOOL "" FORCE)
    # Kept: video and events, audio, joystick/gamepad (with HIDAPI; a Steam Deck user can drive a window with the
    # controller) and file dialogs (an installer's "choose a folder"). Everything else is off to keep the binary small.
    foreach(_off GPU RENDER CAMERA HAPTIC POWER SENSOR TRAY X11_XSCRNSAVER)
        set(SDL_${_off} OFF CACHE BOOL "" FORCE)
    endforeach()
    add_subdirectory(../external/SDL SDL EXCLUDE_FROM_ALL)

    set(RMLC_DESKTOP_SOURCES
        backend/rmlui_c_backend.cpp
        "${RMLUI_BACKENDS_DIR}/RmlUi_Platform_SDL.cpp"
        backend/gl3_renderer.cpp
    )
    set(RMLC_DESKTOP_DEFINITIONS RMLUI_SDL_VERSION_MAJOR=3)
    if(RMLC_DESKTOP_VULKAN)
        list(APPEND RMLC_DESKTOP_SOURCES "${RMLUI_BACKENDS_DIR}/RmlUi_Renderer_VK.cpp")
        list(APPEND RMLC_DESKTOP_DEFINITIONS RMLC_WITH_VULKAN=1)
    endif()
    set(RMLC_DESKTOP_INCLUDES "${RMLUI_BACKENDS_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/backend" "${CMAKE_CURRENT_SOURCE_DIR}/../external/stb")
endif()

# RMLC_SHARED builds rmlui_c as a DLL/shared object with RmlUi, FreeType and (with RMLC_DESKTOP) the desktop backend
# inside it, for a client that loads it at runtime. Otherwise it is a static library for linking into an executable.
option(RMLC_SHARED "Build rmlui_c as a shared library" OFF)

if(RMLC_SHARED)
    add_library(rmlui_c SHARED
        generated/rmlui_c.cpp
        rmlui_c_manual.cpp
    )
    if(RMLC_DESKTOP)
        target_sources(rmlui_c PRIVATE ${RMLC_DESKTOP_SOURCES})
        target_compile_definitions(rmlui_c PRIVATE ${RMLC_DESKTOP_DEFINITIONS})
        target_include_directories(rmlui_c PRIVATE ${RMLC_DESKTOP_INCLUDES})
        target_link_libraries(rmlui_c PRIVATE SDL3::SDL3-static)
    endif()
    if(RMLC_OVERLAY)
        if(NOT WIN32)
            message(FATAL_ERROR "RMLC_OVERLAY is Direct3D 11 and Win32: Windows only")
        endif()
        target_sources(rmlui_c PRIVATE
            overlay/rmlui_c_overlay.cpp
            "${RMLUI_BACKENDS_DIR}/RmlUi_Renderer_DX11.cpp"
            "${RMLUI_BACKENDS_DIR}/RmlUi_Platform_Win32.cpp")
        target_include_directories(rmlui_c PRIVATE "${RMLUI_BACKENDS_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/backend"
            "${CMAKE_CURRENT_SOURCE_DIR}/../external/stb")
        # d3d11 and d3dcompiler (RmlUi compiles its shaders at run time) ship with Windows, as does imm32.
        target_link_libraries(rmlui_c PRIVATE d3d11 d3dcompiler imm32)

        # A stand-in game for testing the overlay (demo/OverlayTest). Test-only; never shipped.
        add_library(rmlc_testgame SHARED overlay/testgame/testgame.cpp)
        target_compile_features(rmlc_testgame PRIVATE cxx_std_20)
        target_link_libraries(rmlc_testgame PRIVATE d3d11 dxgi user32)
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

if(RMLC_DESKTOP AND NOT RMLC_SHARED)
    add_library(rmlui_c_backend STATIC ${RMLC_DESKTOP_SOURCES})
    target_compile_features(rmlui_c_backend PRIVATE cxx_std_20)
    target_compile_definitions(rmlui_c_backend PRIVATE ${RMLC_DESKTOP_DEFINITIONS})
    target_include_directories(rmlui_c_backend PRIVATE ${RMLC_DESKTOP_INCLUDES})
    target_link_libraries(rmlui_c_backend PUBLIC rmlui_c SDL3::SDL3-static)
endif()

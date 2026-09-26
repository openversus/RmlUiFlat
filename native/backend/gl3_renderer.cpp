// RmlUi's OpenGL 3 renderer (Backends/RmlUi_Renderer_GL3.cpp), compiled inside namespace rmlc_gl3.
//
// RmlUi's sample renderers are written to be linked one at a time, and RmlUi_Renderer_GL3.cpp and
// RmlUi_Renderer_VK.cpp define different types under the same names (Gfx::FramebufferData, CompiledFilter, ProgramId,
// ...). Linked together, the linker keeps one copy of each same-named inline function, so one renderer ran the other's
// code for its own types: under MSVC's ABI the Vulkan renderer's framebuffer vector was built with the OpenGL
// renderer's element size and corrupted on its first frame. Wrapping this renderer in its own namespace gives every one
// of its names, present and future, a distinct symbol.
//
// Everything the renderer includes is included first, in its order, so include guards keep those headers outside the
// namespace; only the renderer's own definitions land in it. A header that slips through fails to compile.
#include <RmlUi/Core/Core.h>
#include <RmlUi/Core/DecorationTypes.h>
#include <RmlUi/Core/FileInterface.h>
#include <RmlUi/Core/Geometry.h>
#include <RmlUi/Core/Log.h>
#include <RmlUi/Core/MeshUtilities.h>
#include <RmlUi/Core/Platform.h>
#include <RmlUi/Core/RenderInterface.h>
#include <RmlUi/Core/SystemInterface.h>
#include <RmlUi/Core/Types.h>
// Standard headers the renderer uses directly (std::move, std::swap, std::is_same_v, std::all_of): MSVC's STL does not
// pull them in through RmlUi's headers the way libstdc++ does, so they must be included here, outside the namespace.
#include <algorithm>
#include <bitset>
#include <type_traits>
#include <utility>
#include <inttypes.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
    #ifndef WIN32_LEAN_AND_MEAN
        #define WIN32_LEAN_AND_MEAN
    #endif
    #ifndef NOMINMAX
        #define NOMINMAX
    #endif
    #include <windows.h>
#else
    #include <dlfcn.h>
#endif

// The glad OpenGL loader is C with glad_gl* names, which collide with nothing, and it includes system headers of its
// own (clang's stddef.h declares namespace std). It is compiled here, outside the namespace, and the renderer is told
// through RmlUi's RMLUI_GL3_CUSTOM_LOADER hook that the application supplies the loader, so it neither includes glad
// again nor loads it: Gl3Load and Gl3Unload below do.
#define GLAD_GL_IMPLEMENTATION
#include "RmlUi_Include_GL3.h"
#define RMLUI_GL3_CUSTOM_LOADER "gl3_custom_loader.h"

namespace rmlc_gl3
{
#include "RmlUi_Renderer_GL3.cpp"
}

#include "gl3_renderer.h"
#include "image_loading_renderer.h"

namespace rmlc
{
    using Renderer = ImageLoadingRenderer<rmlc_gl3::RenderInterface_GL3>;

    bool Gl3Load(Rml::String& message)
    {
        const int version = gladLoaderLoadGL();
        if (version == 0)
        {
            message = "Failed to load OpenGL functions for the context.";
            return false;
        }
        message = Rml::CreateString("Loaded OpenGL %d.%d.", GLAD_VERSION_MAJOR(version), GLAD_VERSION_MINOR(version));
        return rmlc_gl3::RmlGL3::Initialize(nullptr);
    }

    void Gl3Unload()
    {
        rmlc_gl3::RmlGL3::Shutdown();
        gladLoaderUnloadGL();
    }

    Rml::RenderInterface* Gl3Create()
    {
        auto* renderer = new Renderer();
        if (!*renderer)
        {
            delete renderer;
            return nullptr;
        }
        return renderer;
    }

    void Gl3Destroy(Rml::RenderInterface* renderer)
    {
        delete static_cast<Renderer*>(renderer);
    }

    void Gl3SetViewport(Rml::RenderInterface* renderer, int width, int height)
    {
        static_cast<Renderer*>(renderer)->SetViewport(width, height);
    }

    void Gl3BeginFrame(Rml::RenderInterface* renderer)
    {
        static_cast<Renderer*>(renderer)->Clear();
        static_cast<Renderer*>(renderer)->BeginFrame();
    }

    void Gl3EndFrame(Rml::RenderInterface* renderer)
    {
        static_cast<Renderer*>(renderer)->EndFrame();
    }
}

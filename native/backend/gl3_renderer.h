// RmlUi's OpenGL 3 renderer behind a factory that exposes none of its types. See gl3_renderer.cpp for why.
#pragma once

#include <RmlUi/Core/RenderInterface.h>
#include <RmlUi/Core/Types.h>

namespace rmlc
{
    /** Loads OpenGL function pointers for the current context (RmlGL3::Initialize). */
    bool Gl3Load(Rml::String& message);
    void Gl3Unload();
    /** A new OpenGL 3 render interface with PNG/JPEG loading, or null if it could not start. */
    Rml::RenderInterface* Gl3Create();
    void Gl3Destroy(Rml::RenderInterface* renderer);
    void Gl3SetViewport(Rml::RenderInterface* renderer, int width, int height);
    /** Clears and begins a frame. */
    void Gl3BeginFrame(Rml::RenderInterface* renderer);
    /** Composites the frame into the window's back buffer (the caller swaps). */
    void Gl3EndFrame(Rml::RenderInterface* renderer);
}

#define RMLC_BUILD
#include "rmlui_c_backend.h"

#include <RmlUi_Backend.h>

RMLC_API bool RmlBackend_Initialize(const char* window_name, int32_t width, int32_t height, bool allow_resize)
{
    return Backend::Initialize(window_name, width, height, allow_resize);
}

RMLC_API void RmlBackend_Shutdown(void)
{
    Backend::Shutdown();
}

RMLC_API RmlSystemInterface* RmlBackend_GetSystemInterface(void)
{
    return reinterpret_cast<RmlSystemInterface*>(Backend::GetSystemInterface());
}

RMLC_API RmlRenderInterface* RmlBackend_GetRenderInterface(void)
{
    return reinterpret_cast<RmlRenderInterface*>(Backend::GetRenderInterface());
}

RMLC_API bool RmlBackend_ProcessEvents(RmlContext* context, bool power_save)
{
    return Backend::ProcessEvents(reinterpret_cast<Rml::Context*>(context), nullptr, power_save);
}

RMLC_API void RmlBackend_RequestExit(void)
{
    Backend::RequestExit();
}

RMLC_API void RmlBackend_BeginFrame(void)
{
    Backend::BeginFrame();
}

RMLC_API void RmlBackend_PresentFrame(void)
{
    Backend::PresentFrame();
}

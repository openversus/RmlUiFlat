/* C API over RmlUi's sample desktop backends (Backends/RmlUi_Backend.h): a window, input, and a render interface.
   For standalone windows only; the game overlay supplies its own render interface. */
#ifndef RMLUI_C_BACKEND_H
#define RMLUI_C_BACKEND_H

#include "../generated/rmlui_c.h"

#ifdef __cplusplus
extern "C" {
#endif

RMLC_API bool RmlBackend_Initialize(const char* window_name, int32_t width, int32_t height, bool allow_resize);
RMLC_API void RmlBackend_Shutdown(void);
RMLC_API RmlSystemInterface* RmlBackend_GetSystemInterface(void);
RMLC_API RmlRenderInterface* RmlBackend_GetRenderInterface(void);
/** Returns false when the window has been asked to close. */
RMLC_API bool RmlBackend_ProcessEvents(RmlContext* context, bool power_save);
RMLC_API void RmlBackend_RequestExit(void);
RMLC_API void RmlBackend_BeginFrame(void);
RMLC_API void RmlBackend_PresentFrame(void);

#ifdef __cplusplus
}
#endif

#endif

/* C API for a standalone RmlUi window: SDL3 for the window and input, RmlUi's Vulkan renderer when the machine can run
   it and its OpenGL 3 renderer otherwise. For standalone windows only (installer, tools); the game overlay supplies its
   own render interface. */
#ifndef RMLUI_C_BACKEND_H
#define RMLUI_C_BACKEND_H

#include "../generated/rmlui_c.h"

#ifdef __cplusplus
extern "C" {
#endif

/** A renderer: what to prefer when initialising, and which one is in use. */
typedef enum RmlBackendRenderer
{
    /** Preference: Vulkan if this machine can run RmlUi's Vulkan renderer, otherwise OpenGL. As a result: none. */
    RmlBackendRenderer_Auto = 0,
    RmlBackendRenderer_Vulkan = 1,
    RmlBackendRenderer_OpenGL = 2,
} RmlBackendRenderer;

/**
 * Opens a window and brings up a renderer. width and height are in density-independent pixels; the window is scaled by
 * the display's content scale. With a specific renderer, fails rather than falling back to the other.
 */
RMLC_API bool RmlBackend_Initialize(const char* window_name, int32_t width, int32_t height, bool allow_resize, RmlBackendRenderer renderer);
RMLC_API void RmlBackend_Shutdown(void);
/** The renderer in use, or RmlBackendRenderer_Auto before Initialize succeeds. */
RMLC_API RmlBackendRenderer RmlBackend_GetRenderer(void);
/** The window's size in physical pixels: the size to give the RmlUi context. */
RMLC_API void RmlBackend_GetPixelSize(int32_t* width, int32_t* height);
/** Physical pixels per density-independent pixel: the context's density-independent pixel ratio. */
RMLC_API float RmlBackend_GetDisplayScale(void);
RMLC_API RmlSystemInterface* RmlBackend_GetSystemInterface(void);
RMLC_API RmlRenderInterface* RmlBackend_GetRenderInterface(void);
/** Returns false when the window has been asked to close. */
RMLC_API bool RmlBackend_ProcessEvents(RmlContext* context, bool power_save);
RMLC_API void RmlBackend_RequestExit(void);
RMLC_API void RmlBackend_BeginFrame(void);
/**
 * Finishes the frame: composites RmlUi's layers into the window's back buffer. For OpenGL the frame is then complete but
 * not yet shown; RmlUi's Vulkan renderer also presents here.
 */
RMLC_API void RmlBackend_EndFrame(void);
/** Shows the finished frame (for OpenGL, swaps buffers; for Vulkan, already done by EndFrame). */
RMLC_API void RmlBackend_PresentFrame(void);

/**
 * The answer to RmlBackend_ShowOpenFolderDialog, given once: the folder chosen (UTF-8), or NULL when the person
 * cancelled; error is NULL unless the picker could not be shown, and then says why. Both strings are valid only during
 * the call. It may run on another thread: the system's picker answers on its own thread on Windows and with zenity on
 * Linux, and during RmlBackend_ProcessEvents with the desktop portal.
 */
typedef void (*RmlBackendFolderChosen)(void* userdata, const char* folder, const char* error);
/**
 * Opens the system's folder picker over the window, starting in default_location (NULL: where the system chooses), and
 * returns at once; callback gets the answer. Call it on the thread that runs the window. With the desktop portal the
 * answer arrives while events are processed, so keep processing them (power save off) until it has.
 */
RMLC_API void RmlBackend_ShowOpenFolderDialog(RmlBackendFolderChosen callback, void* userdata, const char* default_location);

#ifdef __cplusplus
}
#endif

#endif

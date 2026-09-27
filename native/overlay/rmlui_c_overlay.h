/* An RmlUi overlay on a Direct3D 11 swap chain someone else owns (a game's). Hand-written, like the desktop backend:
   RmlUi's DX11 renderer draws with the game's device into the swap chain's back buffer, and the game's device state
   is swapped out around it. Nothing here hooks anything; the C# host (RmlUi.Overlay) calls these from its Present
   hook, or a program that owns its own loop calls them directly.

   Threads: everything that takes a context runs on the thread that presents (the render thread), one call at a time.
   RmlOverlay_GetKeyModifierState is the exception: it reads the calling thread's keyboard state, so it is called on
   the window's thread, as each message arrives.

   Faults: an exception inside RmlUi or the renderer (an access violation, say) is caught. The overlay is then
   disabled for good, the game's device state is restored, and every call returns at once; RmlOverlay_GetError says
   what happened. A game must never go down because its menu did. */
#ifndef RMLUI_C_OVERLAY_H
#define RMLUI_C_OVERLAY_H

#include "generated/rmlui_c.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Creates the renderer on the game's ID3D11Device and a system interface for its window (HWND). False on failure. */
RMLC_API bool RmlOverlay_InitializeD3D11(void* d3d11_device, void* window);

/** Destroys the renderer and system interface. Call after Rml::Shutdown, which still releases textures through them. */
RMLC_API void RmlOverlay_Shutdown(void);

RMLC_API RmlSystemInterface* RmlOverlay_GetSystemInterface(void);
RMLC_API RmlRenderInterface* RmlOverlay_GetRenderInterface(void);

/**
 * Updates and renders the context into the swap chain's (IDXGISwapChain*) current back buffer, with the game's device
 * state saved and restored around it. Resizes the context to the back buffer when that changes. Call just before the
 * game presents. False when the overlay is faulted or the back buffer could not be used.
 */
RMLC_API bool RmlOverlay_RenderD3D11(RmlContext* context, void* swap_chain);

/** Releases the overlay's view of the back buffer. Call before the swap chain's ResizeBuffers; the next render makes a new one. */
RMLC_API void RmlOverlay_ReleaseTargets(void);

/**
 * Feeds one window message to the context: mouse buttons, movement, wheel and leave, key down and up, and characters.
 * key_modifier_state is RmlOverlay_GetKeyModifierState() taken when the message arrived. Returns false when RmlUi
 * handled it (an element took the click or the key), true when the game may have it.
 */
RMLC_API bool RmlOverlay_ProcessMessage(RmlContext* context, uint32_t message, uintptr_t w_param, intptr_t l_param, int32_t key_modifier_state);

/** RmlUi's key modifier flags (Rml::Input::KeyModifier) from the calling thread's keyboard state. Call on the window's thread. */
RMLC_API int32_t RmlOverlay_GetKeyModifierState(void);

/** Whether an exception inside RmlUi or the renderer has disabled the overlay. */
RMLC_API bool RmlOverlay_IsFaulted(void);

/** What went wrong last (initialisation, a back buffer, a fault), or an empty string. */
RMLC_API const char* RmlOverlay_GetError(void);

#ifdef __cplusplus
}
#endif

#endif

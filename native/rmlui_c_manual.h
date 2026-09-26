/* Hand-written part of the RmlUi C API: C++ interfaces that RmlUi calls back into, forwarded to C function pointers.
   Everything else is generated into generated/rmlui_c.h. */
#ifndef RMLUI_C_MANUAL_H
#define RMLUI_C_MANUAL_H

#include "generated/rmlui_c.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Callbacks for an event listener. process_event is required; the others may be null. */
typedef struct RmlEventListenerCallbacks
{
    void* user_data;
    /** Called for each event the listener receives. The event is valid only during the call. */
    void (*process_event)(void* user_data, RmlEvent* event);
    /** Called when the listener is attached to an element. */
    void (*on_attach)(void* user_data, RmlElement* element);
    /** Called when the listener is detached from an element, including when the element is destroyed. */
    void (*on_detach)(void* user_data, RmlElement* element);
} RmlEventListenerCallbacks;

/**
 * Creates a listener that forwards to callbacks, which are copied. Attach it with RmlElement_AddEventListener.
 * The caller owns it: remove it from every element (or let the elements be destroyed) before RmlEventListener_Destroy.
 */
RMLC_API RmlEventListener* RmlEventListener_Create(const RmlEventListenerCallbacks* callbacks);

/** The user_data given to RmlEventListener_Create. */
RMLC_API void* RmlEventListener_GetUserData(RmlEventListener* listener);

/** Destroys a listener made by RmlEventListener_Create. */
RMLC_API void RmlEventListener_Destroy(RmlEventListener* listener);

#ifdef __cplusplus
}
#endif

#endif

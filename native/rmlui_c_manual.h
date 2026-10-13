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

/** Where RmlFileInterfaceCallbacks.load puts a file's contents, with RmlFileBuffer_Assign. */
typedef struct RmlFileBuffer RmlFileBuffer;

/** Callbacks for a file interface that serves whole files. */
typedef struct RmlFileInterfaceCallbacks
{
    void* user_data;
    /**
     * Called with each path RmlUi opens (UTF-8, as RmlUi resolved it against the referencing document). Puts the
     * whole file into out with RmlFileBuffer_Assign and returns nonzero, or returns zero when there is no such file.
     * Required.
     */
    int (*load)(void* user_data, const char* path, RmlFileBuffer* out);
} RmlFileInterfaceCallbacks;

/** Replaces the contents of a buffer passed to RmlFileInterfaceCallbacks.load with a copy of size bytes at data. */
RMLC_API void RmlFileBuffer_Assign(RmlFileBuffer* buffer, const void* data, size_t size);

/**
 * Creates a file interface that asks callbacks.load for each file RmlUi opens and serves reads from the bytes it
 * gets; callbacks are copied. Install it with RmlSetFileInterface before RmlInitialise. The caller owns it: after
 * RmlShutdown, destroy it with RmlFileInterface_Destroy.
 */
RMLC_API RmlFileInterface* RmlFileInterface_Create(const RmlFileInterfaceCallbacks* callbacks);

/** The user_data given to RmlFileInterface_Create. */
RMLC_API void* RmlFileInterface_GetUserData(RmlFileInterface* file_interface);

/** Destroys a file interface made by RmlFileInterface_Create. */
RMLC_API void RmlFileInterface_Destroy(RmlFileInterface* file_interface);

#ifdef __cplusplus
}
#endif

#endif

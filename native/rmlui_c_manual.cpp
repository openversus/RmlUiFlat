#define RMLC_BUILD
#include "rmlui_c_manual.h"

#include <RmlUi/Core/EventListener.h>

namespace
{
    class ForwardingEventListener final : public Rml::EventListener
    {
    public:
        explicit ForwardingEventListener(const RmlEventListenerCallbacks& callbacks) : callbacks(callbacks) {}

        void ProcessEvent(Rml::Event& event) override
        {
            callbacks.process_event(callbacks.user_data, reinterpret_cast<RmlEvent*>(&event));
        }

        void OnAttach(Rml::Element* element) override
        {
            if (callbacks.on_attach)
            {
                callbacks.on_attach(callbacks.user_data, reinterpret_cast<RmlElement*>(element));
            }
        }

        void OnDetach(Rml::Element* element) override
        {
            if (callbacks.on_detach)
            {
                callbacks.on_detach(callbacks.user_data, reinterpret_cast<RmlElement*>(element));
            }
        }

    private:
        RmlEventListenerCallbacks callbacks;
    };
}

RMLC_API RmlEventListener* RmlEventListener_Create(const RmlEventListenerCallbacks* callbacks)
{
    if (!callbacks || !callbacks->process_event)
    {
        return nullptr;
    }
    // Upcast before reinterpreting, so the handle is the Rml::EventListener* RmlUi expects.
    Rml::EventListener* listener = new ForwardingEventListener(*callbacks);
    return reinterpret_cast<RmlEventListener*>(listener);
}

RMLC_API void RmlEventListener_Destroy(RmlEventListener* listener)
{
    delete reinterpret_cast<Rml::EventListener*>(listener);
}

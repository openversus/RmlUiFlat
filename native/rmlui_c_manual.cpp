#define RMLC_BUILD
#include "rmlui_c_manual.h"

#include <RmlUi/Core/EventListener.h>
#include <RmlUi/Core/FileInterface.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <memory>

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

        void* UserData() const { return callbacks.user_data; }

    private:
        RmlEventListenerCallbacks callbacks;
    };

    // Each Open asks the callback for the whole file once; reads, seeks and the length are then served from that copy.
    class ForwardingFileInterface final : public Rml::FileInterface
    {
    public:
        explicit ForwardingFileInterface(const RmlFileInterfaceCallbacks& callbacks) : callbacks(callbacks) {}

        Rml::FileHandle Open(const Rml::String& path) override
        {
            auto file = std::make_unique<OpenFile>();
            if (!Load(path, file->data))
            {
                return 0;
            }
            return reinterpret_cast<Rml::FileHandle>(file.release());
        }

        void Close(Rml::FileHandle file) override { delete AsOpenFile(file); }

        size_t Read(void* buffer, size_t size, Rml::FileHandle handle) override
        {
            OpenFile* file = AsOpenFile(handle);
            const size_t count = std::min(size, file->data.size() - file->position);
            std::memcpy(buffer, file->data.data() + file->position, count);
            file->position += count;
            return count;
        }

        bool Seek(Rml::FileHandle handle, long offset, int origin) override
        {
            OpenFile* file = AsOpenFile(handle);
            long long base;
            switch (origin)
            {
            case SEEK_SET: base = 0; break;
            case SEEK_CUR: base = static_cast<long long>(file->position); break;
            case SEEK_END: base = static_cast<long long>(file->data.size()); break;
            default: return false;
            }
            const long long target = base + offset;
            if (target < 0 || target > static_cast<long long>(file->data.size()))
            {
                return false;
            }
            file->position = static_cast<size_t>(target);
            return true;
        }

        size_t Tell(Rml::FileHandle handle) override { return AsOpenFile(handle)->position; }

        size_t Length(Rml::FileHandle handle) override { return AsOpenFile(handle)->data.size(); }

        bool LoadFile(const Rml::String& path, Rml::String& out_data) override { return Load(path, out_data); }

        void* UserData() const { return callbacks.user_data; }

    private:
        struct OpenFile
        {
            Rml::String data;
            size_t position = 0;
        };

        static OpenFile* AsOpenFile(Rml::FileHandle handle) { return reinterpret_cast<OpenFile*>(handle); }

        bool Load(const Rml::String& path, Rml::String& out)
        {
            out.clear();
            return callbacks.load(callbacks.user_data, path.c_str(), reinterpret_cast<RmlFileBuffer*>(&out)) != 0;
        }

        RmlFileInterfaceCallbacks callbacks;
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

RMLC_API void* RmlEventListener_GetUserData(RmlEventListener* listener)
{
    // Only listeners from RmlEventListener_Create reach here, so the downcast is to the type that was created.
    return static_cast<ForwardingEventListener*>(reinterpret_cast<Rml::EventListener*>(listener))->UserData();
}

RMLC_API void RmlEventListener_Destroy(RmlEventListener* listener)
{
    delete reinterpret_cast<Rml::EventListener*>(listener);
}

RMLC_API void RmlFileBuffer_Assign(RmlFileBuffer* buffer, const void* data, size_t size)
{
    auto& out = *reinterpret_cast<Rml::String*>(buffer);
    if (size == 0)
    {
        out.clear();
        return;
    }
    out.assign(static_cast<const char*>(data), size);
}

RMLC_API RmlFileInterface* RmlFileInterface_Create(const RmlFileInterfaceCallbacks* callbacks)
{
    if (!callbacks || !callbacks->load)
    {
        return nullptr;
    }
    // Upcast before reinterpreting, so the handle is the Rml::FileInterface* RmlUi expects.
    Rml::FileInterface* file_interface = new ForwardingFileInterface(*callbacks);
    return reinterpret_cast<RmlFileInterface*>(file_interface);
}

RMLC_API void* RmlFileInterface_GetUserData(RmlFileInterface* file_interface)
{
    // Only file interfaces from RmlFileInterface_Create reach here, so the downcast is to the type that was created.
    return static_cast<ForwardingFileInterface*>(reinterpret_cast<Rml::FileInterface*>(file_interface))->UserData();
}

RMLC_API void RmlFileInterface_Destroy(RmlFileInterface* file_interface)
{
    delete reinterpret_cast<Rml::FileInterface*>(file_interface);
}

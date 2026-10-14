// A standalone RmlUi window: SDL3 for the window and input, RmlUi's Vulkan renderer when this machine can run it, its
// OpenGL 3 renderer otherwise. Assembled from RmlUi's Backends/RmlUi_Backend_SDL_VK.cpp and RmlUi_Backend_SDL_GL3.cpp,
// which each hard-wire one renderer and so cannot be linked together.
#define RMLC_BUILD
#include "rmlui_c_backend.h"

#include <RmlUi/Core.h>
#include <RmlUi/Core/FileInterface.h>

#include "RmlUi_Platform_SDL.h"
#include "gl3_renderer.h"
#if RMLC_WITH_VULKAN
    #include "RmlUi_Renderer_VK.h"
    #include <SDL3/SDL_vulkan.h>
#endif
#include <SDL3/SDL.h>

#include <cstdio>
#include <cstring>
#include <memory>

#define STB_IMAGE_IMPLEMENTATION
#include "image_loading_renderer.h"

namespace
{
    struct State
    {
        SDL_Window* window = nullptr;
        SDL_GLContext gl_context = nullptr;
        std::unique_ptr<SystemInterface_SDL> system_interface;
        // Owned; created and destroyed through gl3_renderer.h, which keeps RmlUi's GL3 renderer in its own namespace.
        Rml::RenderInterface* gl3 = nullptr;
#if RMLC_WITH_VULKAN
        std::unique_ptr<ImageLoadingRenderer<RenderInterface_VK>> vk;
#endif
        TextInputMethodEditor_SDL text_input;
        RmlBackendRenderer renderer = RmlBackendRenderer_Auto;
        bool running = true;

        Rml::RenderInterface* RenderInterface()
        {
#if RMLC_WITH_VULKAN
            if (vk)
            {
                return vk.get();
            }
#endif
            return gl3;
        }

        void SetViewport(int width, int height)
        {
#if RMLC_WITH_VULKAN
            if (vk)
            {
                vk->SetViewport(width, height);
                return;
            }
#endif
            rmlc::Gl3SetViewport(gl3, width, height);
        }
    };

    // A plain pointer, freed only by RmlBackend_Shutdown. As a static unique_ptr it would be destroyed when the library
    // unloads, and a process that exits without Shutdown (an unhandled exception, say) would tear the renderer down
    // after the graphics driver is already gone.
    State* state = nullptr;

    SDL_Window* OpenWindow(const char* title, int width, int height, bool allow_resize, bool vulkan)
    {
        const float scale = SDL_GetDisplayContentScale(SDL_GetPrimaryDisplay());
        SDL_PropertiesID props = SDL_CreateProperties();
        SDL_SetStringProperty(props, SDL_PROP_WINDOW_CREATE_TITLE_STRING, title);
        SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_X_NUMBER, SDL_WINDOWPOS_CENTERED);
        SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_Y_NUMBER, SDL_WINDOWPOS_CENTERED);
        SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, int(width * (scale > 0 ? scale : 1.f)));
        SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, int(height * (scale > 0 ? scale : 1.f)));
        SDL_SetBooleanProperty(props, vulkan ? SDL_PROP_WINDOW_CREATE_VULKAN_BOOLEAN : SDL_PROP_WINDOW_CREATE_OPENGL_BOOLEAN, true);
        SDL_SetBooleanProperty(props, SDL_PROP_WINDOW_CREATE_RESIZABLE_BOOLEAN, allow_resize);
        SDL_SetBooleanProperty(props, SDL_PROP_WINDOW_CREATE_HIGH_PIXEL_DENSITY_BOOLEAN, true);
        SDL_Window* window = SDL_CreateWindowWithProperties(props);
        SDL_DestroyProperties(props);
        if (!window)
        {
            Rml::Log::Message(Rml::Log::LT_ERROR, "SDL could not create a %s window: %s", vulkan ? "Vulkan" : "OpenGL", SDL_GetError());
        }
        return window;
    }

    bool StartOpenGL(const char* title, int width, int height, bool allow_resize)
    {
        SDL_GL_SetAttribute(SDL_GL_CONTEXT_FLAGS, 0);
        SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_CORE);
        SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GL_DOUBLEBUFFER, 1);
        // RmlUi's GL3 renderer draws into its own framebuffers (with their own depth and stencil) and composites into
        // the window's, which therefore needs neither. SDL defaults to a 16-bit depth buffer, which not every WGL
        // implementation offers on a window format (Wine's EGL-backed one does not).
        SDL_GL_SetAttribute(SDL_GL_DEPTH_SIZE, 0);
        SDL_GL_SetAttribute(SDL_GL_STENCIL_SIZE, 0);

        SDL_Window* window = OpenWindow(title, width, height, allow_resize, false);
        if (!window)
        {
            return false;
        }
        SDL_GLContext context = SDL_GL_CreateContext(window);
        if (!context)
        {
            Rml::Log::Message(Rml::Log::LT_ERROR, "SDL could not create an OpenGL 3.3 core context: %s", SDL_GetError());
            SDL_DestroyWindow(window);
            return false;
        }
        SDL_GL_MakeCurrent(window, context);
        SDL_GL_SetSwapInterval(1);

        Rml::String message;
        if (!rmlc::Gl3Load(message))
        {
            Rml::Log::Message(Rml::Log::LT_ERROR, "OpenGL renderer: %s", message.c_str());
            SDL_GL_DestroyContext(context);
            SDL_DestroyWindow(window);
            return false;
        }
        Rml::RenderInterface* renderer = rmlc::Gl3Create();
        if (!renderer)
        {
            Rml::Log::Message(Rml::Log::LT_ERROR, "RmlUi's OpenGL 3 renderer could not start");
            rmlc::Gl3Unload();
            SDL_GL_DestroyContext(context);
            SDL_DestroyWindow(window);
            return false;
        }

        state->window = window;
        state->gl_context = context;
        state->gl3 = renderer;
        state->renderer = RmlBackendRenderer_OpenGL;
        return true;
    }

#if RMLC_WITH_VULKAN
    // RmlUi's Vulkan renderer cannot fail cleanly: RenderInterface_VK::Initialize returns true unconditionally, and in a
    // release build its checks (RMLUI_VK_ASSERTMSG) compile to nothing, so a machine without working Vulkan crashes
    // inside it. This probe asks the same questions first, on a throwaway instance and device, so Auto can fall back
    // to OpenGL instead.
    //
    // It mirrors RmlUi_Renderer_VK.cpp as of RmlUi 3045e6e: Initialize_Instance (API version RMLUI_VK_API_VERSION),
    // Initialize_PhysicalDevice (first discrete, else integrated, else CPU device), Initialize_QueueIndecies (a graphics
    // queue that can present, or graphics and present separately), and Initialize_Device (VK_KHR_swapchain, the four
    // VkPhysicalDeviceFeatures and shaderSubgroupExtendedTypes). It creates the device with a graphics queue only,
    // where RmlUi may add a compute one. After an RmlUi update, compare Initialize_Device's request with this one.
    bool ProbeVulkan(SDL_Window* window, Rml::String& reason)
    {
        if (gladLoaderLoadVulkan(VK_NULL_HANDLE, VK_NULL_HANDLE, VK_NULL_HANDLE) == 0)
        {
            reason = "the Vulkan loader could not be loaded";
            return false;
        }

        Uint32 extension_count = 0;
        const char* const* extensions = SDL_Vulkan_GetInstanceExtensions(&extension_count);
        if (!extensions)
        {
            reason = Rml::CreateString("SDL has no Vulkan instance extensions: %s", SDL_GetError());
            return false;
        }
        VkApplicationInfo app = {};
        app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
        app.pApplicationName = "RmlUi probe";
        app.apiVersion = RMLUI_VK_API_VERSION;
        VkInstanceCreateInfo instance_info = {};
        instance_info.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        instance_info.pApplicationInfo = &app;
        instance_info.enabledExtensionCount = extension_count;
        instance_info.ppEnabledExtensionNames = extensions;
        VkInstance instance = VK_NULL_HANDLE;
        VkResult result = vkCreateInstance(&instance_info, nullptr, &instance);
        if (result != VK_SUCCESS)
        {
            reason = Rml::CreateString("vkCreateInstance failed (%d)", int(result));
            return false;
        }
        gladLoaderLoadVulkan(instance, VK_NULL_HANDLE, VK_NULL_HANDLE);

        VkSurfaceKHR surface = VK_NULL_HANDLE;
        VkDevice device = VK_NULL_HANDLE;
        auto finish = [&](bool ok) {
            if (device)
            {
                vkDestroyDevice(device, nullptr);
            }
            if (surface)
            {
                SDL_Vulkan_DestroySurface(instance, surface, nullptr);
            }
            vkDestroyInstance(instance, nullptr);
            return ok;
        };

        uint32_t device_count = 0;
        vkEnumeratePhysicalDevices(instance, &device_count, nullptr);
        Rml::Vector<VkPhysicalDevice> devices(device_count);
        vkEnumeratePhysicalDevices(instance, &device_count, devices.data());
        VkPhysicalDevice physical = VK_NULL_HANDLE;
        for (VkPhysicalDeviceType type : {VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU, VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU, VK_PHYSICAL_DEVICE_TYPE_CPU})
        {
            for (VkPhysicalDevice candidate : devices)
            {
                VkPhysicalDeviceProperties properties;
                vkGetPhysicalDeviceProperties(candidate, &properties);
                if (properties.deviceType == type)
                {
                    physical = candidate;
                    break;
                }
            }
            if (physical)
            {
                break;
            }
        }
        if (!physical)
        {
            reason = Rml::CreateString("no discrete, integrated or CPU Vulkan device among %u", device_count);
            return finish(false);
        }
        gladLoaderLoadVulkan(instance, physical, VK_NULL_HANDLE);

        if (!SDL_Vulkan_CreateSurface(window, instance, nullptr, &surface))
        {
            reason = Rml::CreateString("SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
            return finish(false);
        }

        uint32_t family_count = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &family_count, nullptr);
        Rml::Vector<VkQueueFamilyProperties> families(family_count);
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &family_count, families.data());
        uint32_t graphics = UINT32_MAX;
        bool can_present = false;
        for (uint32_t i = 0; i < family_count; ++i)
        {
            VkBool32 present = VK_FALSE;
            vkGetPhysicalDeviceSurfaceSupportKHR(physical, i, surface, &present);
            can_present = can_present || present == VK_TRUE;
            if ((families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) != 0 && (graphics == UINT32_MAX || present == VK_TRUE))
            {
                graphics = i;
            }
        }
        if (graphics == UINT32_MAX || !can_present)
        {
            reason = graphics == UINT32_MAX ? "no Vulkan graphics queue" : "no Vulkan queue can present to the window";
            return finish(false);
        }

        uint32_t extension_property_count = 0;
        vkEnumerateDeviceExtensionProperties(physical, nullptr, &extension_property_count, nullptr);
        Rml::Vector<VkExtensionProperties> available(extension_property_count);
        vkEnumerateDeviceExtensionProperties(physical, nullptr, &extension_property_count, available.data());
        auto has = [&](const char* name) {
            for (const auto& e : available)
            {
                if (strcmp(e.extensionName, name) == 0)
                {
                    return true;
                }
            }
            return false;
        };
        if (!has(VK_KHR_SWAPCHAIN_EXTENSION_NAME))
        {
            reason = "the Vulkan device has no VK_KHR_swapchain";
            return finish(false);
        }
        Rml::Vector<const char*> device_extensions = {VK_KHR_SWAPCHAIN_EXTENSION_NAME};
        for (const char* optional : {"VK_KHR_portability_subset", VK_EXT_SCALAR_BLOCK_LAYOUT_EXTENSION_NAME})
        {
            if (has(optional))
            {
                device_extensions.push_back(optional);
            }
        }

        float priority = 0.0f;
        VkDeviceQueueCreateInfo queue = {};
        queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        queue.queueFamilyIndex = graphics;
        queue.queueCount = 1;
        queue.pQueuePriorities = &priority;
        VkPhysicalDeviceShaderSubgroupExtendedTypesFeaturesKHR subgroup = {};
        subgroup.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_SUBGROUP_EXTENDED_TYPES_FEATURES_KHR;
        subgroup.shaderSubgroupExtendedTypes = VK_TRUE;
        VkPhysicalDeviceFeatures2 features = {};
        features.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
        features.pNext = &subgroup;
        features.features.fillModeNonSolid = VK_TRUE;
        features.features.fragmentStoresAndAtomics = VK_TRUE;
        features.features.vertexPipelineStoresAndAtomics = VK_TRUE;
        features.features.shaderImageGatherExtended = VK_TRUE;
        VkDeviceCreateInfo device_info = {};
        device_info.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        device_info.pNext = &features;
        device_info.queueCreateInfoCount = 1;
        device_info.pQueueCreateInfos = &queue;
        device_info.enabledExtensionCount = uint32_t(device_extensions.size());
        device_info.ppEnabledExtensionNames = device_extensions.data();
        result = vkCreateDevice(physical, &device_info, nullptr, &device);
        if (result != VK_SUCCESS)
        {
            device = VK_NULL_HANDLE;
            reason = Rml::CreateString("vkCreateDevice with RmlUi's features failed (%d)", int(result));
            return finish(false);
        }
        gladLoaderLoadVulkan(instance, physical, device);
        return finish(true);
    }

    bool StartVulkan(const char* title, int width, int height, bool allow_resize)
    {
        SDL_Window* window = OpenWindow(title, width, height, allow_resize, true);
        if (!window)
        {
            return false;
        }
        Rml::String reason;
        if (!ProbeVulkan(window, reason))
        {
            Rml::Log::Message(Rml::Log::LT_WARNING, "Vulkan is not usable here: %s", reason.c_str());
            SDL_DestroyWindow(window);
            return false;
        }

        Uint32 count = 0;
        const char* const* list = SDL_Vulkan_GetInstanceExtensions(&count);
        Rml::Vector<const char*> extensions(list, list + count);
        state->window = window;
        auto renderer = std::make_unique<ImageLoadingRenderer<RenderInterface_VK>>();
        auto create_surface = [](VkInstance instance, VkSurfaceKHR* out_surface) {
            return SDL_Vulkan_CreateSurface(state->window, instance, nullptr, out_surface);
        };
        if (!renderer->Initialize(std::move(extensions), create_surface))
        {
            Rml::Log::Message(Rml::Log::LT_ERROR, "RmlUi's Vulkan renderer could not start");
            state->window = nullptr;
            SDL_DestroyWindow(window);
            return false;
        }
        state->vk = std::move(renderer);
        state->renderer = RmlBackendRenderer_Vulkan;
        return true;
    }
#endif
}

RMLC_API bool RmlBackend_Initialize(const char* window_name, int32_t width, int32_t height, bool allow_resize, RmlBackendRenderer renderer)
{
    if (state)
    {
        return false;
    }
    SDL_SetHint(SDL_HINT_IME_IMPLEMENTED_UI, "composition");
    SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
    SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
    if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_EVENTS))
    {
        Rml::Log::Message(Rml::Log::LT_ERROR, "SDL_Init failed: %s", SDL_GetError());
        return false;
    }
    state = new State();

    bool started = false;
#if RMLC_WITH_VULKAN
    if (renderer == RmlBackendRenderer_Auto || renderer == RmlBackendRenderer_Vulkan)
    {
        started = StartVulkan(window_name, width, height, allow_resize);
    }
#endif
    if (!started && (renderer == RmlBackendRenderer_Auto || renderer == RmlBackendRenderer_OpenGL))
    {
        started = StartOpenGL(window_name, width, height, allow_resize);
    }
    if (!started)
    {
        delete state;
        state = nullptr;
        SDL_Quit();
        return false;
    }

    state->system_interface = std::make_unique<SystemInterface_SDL>(state->window);
    int pixel_width = 0, pixel_height = 0;
    SDL_GetWindowSizeInPixels(state->window, &pixel_width, &pixel_height);
    state->SetViewport(pixel_width, pixel_height);
    Rml::SetTextInputHandler(&state->text_input);
    return true;
}

RMLC_API void RmlBackend_Shutdown(void)
{
    if (!state)
    {
        return;
    }
    Rml::SetTextInputHandler(nullptr);
#if RMLC_WITH_VULKAN
    if (state->vk)
    {
        state->vk->Shutdown();
        state->vk.reset();
    }
#endif
    if (state->gl3)
    {
        rmlc::Gl3Destroy(state->gl3);
        state->gl3 = nullptr;
        rmlc::Gl3Unload();
        SDL_GL_DestroyContext(state->gl_context);
    }
    SDL_DestroyWindow(state->window);
    delete state;
    state = nullptr;
    SDL_Quit();
}

RMLC_API RmlBackendRenderer RmlBackend_GetRenderer(void)
{
    return state ? state->renderer : RmlBackendRenderer_Auto;
}

RMLC_API void RmlBackend_GetPixelSize(int32_t* width, int32_t* height)
{
    int w = 0, h = 0;
    if (state)
    {
        SDL_GetWindowSizeInPixels(state->window, &w, &h);
    }
    if (width)
    {
        *width = w;
    }
    if (height)
    {
        *height = h;
    }
}

RMLC_API float RmlBackend_GetDisplayScale(void)
{
    const float scale = state ? SDL_GetWindowDisplayScale(state->window) : 0.f;
    return scale > 0.f ? scale : 1.f;
}

RMLC_API RmlSystemInterface* RmlBackend_GetSystemInterface(void)
{
    return state ? reinterpret_cast<RmlSystemInterface*>(static_cast<Rml::SystemInterface*>(state->system_interface.get())) : nullptr;
}

RMLC_API RmlRenderInterface* RmlBackend_GetRenderInterface(void)
{
    return state ? reinterpret_cast<RmlRenderInterface*>(state->RenderInterface()) : nullptr;
}

#if RMLC_WITH_VULKAN
// From RmlUi_Backend_SDL_VK.cpp: a minimised or resized window leaves the swapchain unusable until it is recreated.
static bool WaitForValidSwapchain()
{
    bool result = true;
    while (!state->vk->IsSwapchainValid())
    {
        SDL_Event ev;
        while (SDL_PollEvent(&ev))
        {
            if (ev.type == SDL_EVENT_QUIT)
            {
                SDL_RestoreWindow(state->window);
                result = false;
            }
        }
        SDL_Delay(10);
        state->vk->RecreateSwapchain();
    }
    return result;
}
#endif

RMLC_API bool RmlBackend_ProcessEvents(RmlContext* context_handle, bool power_save)
{
    if (!state || !context_handle)
    {
        return false;
    }
    Rml::Context* context = reinterpret_cast<Rml::Context*>(context_handle);

    bool result = state->running;
    state->running = true;

    SDL_Event ev;
    bool has_event = power_save
        ? SDL_WaitEventTimeout(&ev, static_cast<int>(Rml::Math::Min(context->GetNextUpdateDelay(), 10.0) * 1000))
        : SDL_PollEvent(&ev);
    while (has_event)
    {
        bool propagate = true;
        switch (ev.type)
        {
        case SDL_EVENT_QUIT:
            propagate = false;
            result = false;
            break;
        case SDL_EVENT_TEXT_EDITING:
            propagate = false;
            state->text_input.HandleEdit(ev.edit);
            break;
        case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
            // The context's own resize is handled by RmlSDL::InputEventHandler below.
            state->SetViewport(ev.window.data1, ev.window.data2);
            break;
        default:
            break;
        }
        if (propagate)
        {
            RmlSDL::InputEventHandler(context, state->window, ev);
        }
        has_event = SDL_PollEvent(&ev);
    }

#if RMLC_WITH_VULKAN
    if (state->vk && !WaitForValidSwapchain())
    {
        result = false;
    }
#endif
    return result;
}

namespace
{
    struct FolderRequest
    {
        RmlBackendFolderChosen callback;
        void* userdata;
    };

    // SDL's answer: no list for an error, an empty list for a cancel; zenity cancels with an empty name instead.
    void SDLCALL FolderChosen(void* userdata, const char* const* filelist, int /*filter*/)
    {
        auto* request = static_cast<FolderRequest*>(userdata);
        if (!filelist)
        {
            request->callback(request->userdata, nullptr, SDL_GetError());
        }
        else
        {
            request->callback(request->userdata, filelist[0] && filelist[0][0] ? filelist[0] : nullptr, nullptr);
        }
        delete request;
    }
}

RMLC_API void RmlBackend_ShowOpenFolderDialog(RmlBackendFolderChosen callback, void* userdata, const char* default_location)
{
    if (!callback)
    {
        return;
    }
    SDL_ShowOpenFolderDialog(FolderChosen, new FolderRequest{callback, userdata}, state ? state->window : nullptr, default_location, false);
}

RMLC_API void RmlBackend_RequestExit(void)
{
    if (state)
    {
        state->running = false;
    }
}

RMLC_API void RmlBackend_BeginFrame(void)
{
    if (!state)
    {
        return;
    }
#if RMLC_WITH_VULKAN
    if (state->vk)
    {
        state->vk->BeginFrame();
        return;
    }
#endif
    rmlc::Gl3BeginFrame(state->gl3);
}

RMLC_API void RmlBackend_EndFrame(void)
{
    if (!state)
    {
        return;
    }
#if RMLC_WITH_VULKAN
    if (state->vk)
    {
        state->vk->EndFrame();
        return;
    }
#endif
    rmlc::Gl3EndFrame(state->gl3);
}

RMLC_API void RmlBackend_PresentFrame(void)
{
    if (state && state->gl3)
    {
        SDL_GL_SwapWindow(state->window);
    }
}

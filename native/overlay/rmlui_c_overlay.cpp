// Hand-written: RmlUi's DX11 renderer and Win32 system interface behind the C API in rmlui_c_overlay.h.
#define RMLC_BUILD
#include "rmlui_c_overlay.h"

#include <RmlUi/Core.h>
#include <RmlUi_Platform_Win32.h>
#include <RmlUi_Renderer_DX11.h>
#include <cstdio>
#include <d3d11_1.h>
#include <dxgi.h>

#define STB_IMAGE_IMPLEMENTATION
#include "image_loading_renderer.h"

namespace {

using Renderer = ImageLoadingRenderer<RenderInterface_DX11>;

struct OverlayState
{
    ID3D11Device1* device = nullptr;
    ID3D11DeviceContext1* context = nullptr;
    // The device state RmlUi draws with, swapped in for each render so the game's state is never touched.
    ID3DDeviceContextState* own_state = nullptr;
    Renderer* renderer = nullptr;
    SystemInterface_Win32* system = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    int target_width = 0;
    int target_height = 0;
    int viewport_width = 0;
    int viewport_height = 0;
    wchar_t first_surrogate = 0;
    bool faulted = false;
};

// A plain pointer, not a static object, so nothing runs at DLL unload, whatever state the game unloads us in.
OverlayState* state = nullptr;
char error_text[512] = "";

void SetError(const char* what, long code)
{
    std::snprintf(error_text, sizeof error_text, "%s (0x%08lX)", what, static_cast<unsigned long>(code));
}

void Fault(const char* during, unsigned long code)
{
    state->faulted = true;
    std::snprintf(error_text, sizeof error_text, "RmlUi overlay disabled: exception 0x%08lX while %s", code, during);
}

// A view of a typeless back buffer needs a concrete format; the rest are used as they are.
DXGI_FORMAT ViewFormat(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return format;
    }
}

bool EnsureTarget(IDXGISwapChain* swap_chain)
{
    if (state->target)
    {
        return true;
    }
    ID3D11Texture2D* back_buffer = nullptr;
    HRESULT result = swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back_buffer));
    if (FAILED(result))
    {
        SetError("the swap chain's back buffer is not a D3D11 texture", result);
        return false;
    }
    D3D11_TEXTURE2D_DESC desc = {};
    back_buffer->GetDesc(&desc);
    D3D11_RENDER_TARGET_VIEW_DESC view = {};
    view.Format = ViewFormat(desc.Format);
    view.ViewDimension = desc.SampleDesc.Count > 1 ? D3D11_RTV_DIMENSION_TEXTURE2DMS : D3D11_RTV_DIMENSION_TEXTURE2D;
    result = state->device->CreateRenderTargetView(back_buffer, &view, &state->target);
    back_buffer->Release();
    if (FAILED(result))
    {
        SetError("could not create a render target view of the back buffer", result);
        return false;
    }
    state->target_width = static_cast<int>(desc.Width);
    state->target_height = static_cast<int>(desc.Height);
    return true;
}

bool Render(Rml::Context* context, IDXGISwapChain* swap_chain)
{
    if (!EnsureTarget(swap_chain))
    {
        return false;
    }
    if (state->viewport_width != state->target_width || state->viewport_height != state->target_height)
    {
        state->viewport_width = state->target_width;
        state->viewport_height = state->target_height;
        state->renderer->SetViewport(state->target_width, state->target_height);
        context->SetDimensions({state->target_width, state->target_height});
    }
    context->Update();
    state->renderer->BeginFrame();
    context->Render();
    state->renderer->EndFrame(state->target);
    return true;
}

Renderer* CreateRenderer(ID3D11Device1* device)
{
    return new Renderer(device);
}

// The part of RmlWin32::WindowProcedure an overlay can use, with the modifier state passed in: that procedure reads
// GetKeyState, which is the calling thread's, and this runs on the render thread. Mouse capture and leave tracking
// are the game window's business and are left out.
bool Process(Rml::Context* context, uint32_t message, uintptr_t w_param, intptr_t l_param, int modifiers)
{
    switch (message)
    {
    case WM_LBUTTONDOWN: return context->ProcessMouseButtonDown(0, modifiers);
    case WM_LBUTTONUP: return context->ProcessMouseButtonUp(0, modifiers);
    case WM_RBUTTONDOWN: return context->ProcessMouseButtonDown(1, modifiers);
    case WM_RBUTTONUP: return context->ProcessMouseButtonUp(1, modifiers);
    case WM_MBUTTONDOWN: return context->ProcessMouseButtonDown(2, modifiers);
    case WM_MBUTTONUP: return context->ProcessMouseButtonUp(2, modifiers);
    case WM_MOUSEMOVE:
        return context->ProcessMouseMove(static_cast<short>(LOWORD(l_param)), static_cast<short>(HIWORD(l_param)), modifiers);
    case WM_MOUSEWHEEL:
        return context->ProcessMouseWheel(static_cast<float>(static_cast<short>(HIWORD(w_param))) / static_cast<float>(-WHEEL_DELTA), modifiers);
    case WM_MOUSELEAVE: return context->ProcessMouseLeave();
    case WM_KEYDOWN: return context->ProcessKeyDown(RmlWin32::ConvertKey(static_cast<int>(w_param)), modifiers);
    case WM_KEYUP: return context->ProcessKeyUp(RmlWin32::ConvertKey(static_cast<int>(w_param)), modifiers);
    case WM_CHAR:
    {
        const auto unit = static_cast<wchar_t>(w_param);
        if (unit >= 0xD800 && unit < 0xDC00)
        {
            // The first half of a character outside the BMP; the second comes in the next WM_CHAR.
            state->first_surrogate = unit;
            return true;
        }
        char32_t code_point = unit;
        if (unit >= 0xDC00 && unit < 0xE000 && state->first_surrogate != 0)
        {
            code_point = 0x10000 + ((static_cast<char32_t>(state->first_surrogate) - 0xD800) << 10) + (unit - 0xDC00);
        }
        else if (unit == '\r')
        {
            code_point = '\n';
        }
        state->first_surrogate = 0;
        if ((code_point >= 32 || code_point == '\n') && code_point != 127)
        {
            return context->ProcessTextInput(static_cast<Rml::Character>(code_point));
        }
        return true;
    }
    default: return true;
    }
}

void Release()
{
    if (state->target)
    {
        state->target->Release();
    }
    if (state->own_state)
    {
        state->own_state->Release();
    }
    if (state->context)
    {
        state->context->Release();
    }
    if (state->device)
    {
        state->device->Release();
    }
    delete state->system;
    delete state;
    state = nullptr;
}

} // namespace

// The functions below guard RmlUi with structured exception handling (__try/__except). They hold no C++ objects
// that need unwinding; the work is in the functions above.

extern "C" RMLC_API bool RmlOverlay_InitializeD3D11(void* d3d11_device, void* window)
{
    if (state)
    {
        SetError("the overlay is already initialised", 0);
        return false;
    }
    if (!d3d11_device)
    {
        SetError("no device", 0);
        return false;
    }
    state = new OverlayState();
    HRESULT result = static_cast<ID3D11Device*>(d3d11_device)->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&state->device));
    if (FAILED(result))
    {
        SetError("the device is not Direct3D 11.1 (no ID3D11Device1)", result);
        Release();
        return false;
    }
    state->device->GetImmediateContext1(&state->context);
    D3D_FEATURE_LEVEL level = state->device->GetFeatureLevel();
    result = state->device->CreateDeviceContextState(0, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device1), nullptr, &state->own_state);
    if (FAILED(result))
    {
        SetError("could not create a device context state for the overlay", result);
        Release();
        return false;
    }

    ID3DDeviceContextState* previous = nullptr;
    unsigned long code = 0;
    __try
    {
        state->context->SwapDeviceContextState(state->own_state, &previous);
        state->renderer = CreateRenderer(state->device);
    }
    __except (code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
    {
        std::snprintf(error_text, sizeof error_text, "exception 0x%08lX creating RmlUi's DX11 renderer", code);
    }
    if (previous)
    {
        state->context->SwapDeviceContextState(previous, nullptr);
        previous->Release();
    }
    if (!state->renderer)
    {
        Release();
        return false;
    }

    state->system = new SystemInterface_Win32();
    state->system->SetWindow(static_cast<HWND>(window));
    error_text[0] = '\0';
    return true;
}

extern "C" RMLC_API void RmlOverlay_Shutdown(void)
{
    if (!state)
    {
        return;
    }
    // A faulted renderer is left alone: its state is unknown, and leaking it is safer than touching it.
    if (!state->faulted && state->renderer)
    {
        ID3DDeviceContextState* previous = nullptr;
        __try
        {
            state->context->SwapDeviceContextState(state->own_state, &previous);
            delete state->renderer;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
        }
        if (previous)
        {
            state->context->SwapDeviceContextState(previous, nullptr);
            previous->Release();
        }
    }
    Release();
}

extern "C" RMLC_API RmlSystemInterface* RmlOverlay_GetSystemInterface(void)
{
    return state ? reinterpret_cast<RmlSystemInterface*>(static_cast<Rml::SystemInterface*>(state->system)) : nullptr;
}

extern "C" RMLC_API RmlRenderInterface* RmlOverlay_GetRenderInterface(void)
{
    return state ? reinterpret_cast<RmlRenderInterface*>(static_cast<Rml::RenderInterface*>(state->renderer)) : nullptr;
}

extern "C" RMLC_API bool RmlOverlay_RenderD3D11(RmlContext* context, void* swap_chain)
{
    if (!state || state->faulted || !context || !swap_chain)
    {
        return false;
    }
    ID3DDeviceContextState* previous = nullptr;
    bool rendered = false;
    unsigned long code = 0;
    __try
    {
        state->context->SwapDeviceContextState(state->own_state, &previous);
        rendered = Render(reinterpret_cast<Rml::Context*>(context), static_cast<IDXGISwapChain*>(swap_chain));
    }
    __except (code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
    {
        Fault("rendering", code);
    }
    if (previous)
    {
        state->context->SwapDeviceContextState(previous, nullptr);
        previous->Release();
    }
    return rendered && !state->faulted;
}

extern "C" RMLC_API void RmlOverlay_ReleaseTargets(void)
{
    if (state && state->target)
    {
        state->target->Release();
        state->target = nullptr;
    }
}

extern "C" RMLC_API bool RmlOverlay_ProcessMessage(RmlContext* context, uint32_t message, uintptr_t w_param, intptr_t l_param, int32_t key_modifier_state)
{
    if (!state || state->faulted || !context)
    {
        return true;
    }
    bool propagate = true;
    unsigned long code = 0;
    __try
    {
        propagate = Process(reinterpret_cast<Rml::Context*>(context), message, w_param, l_param, key_modifier_state);
    }
    __except (code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
    {
        Fault("processing input", code);
    }
    return propagate;
}

extern "C" RMLC_API int32_t RmlOverlay_GetKeyModifierState(void)
{
    return RmlWin32::GetKeyModifierState();
}

extern "C" RMLC_API bool RmlOverlay_IsFaulted(void)
{
    return state && state->faulted;
}

extern "C" RMLC_API const char* RmlOverlay_GetError(void)
{
    return error_text;
}

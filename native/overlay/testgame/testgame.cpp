// A stand-in for a game, for testing the overlay without one: a window, a Direct3D 11 device and a flip-model swap
// chain (as modern games use), a frame that clears to a colour that changes over time and fills a moving rectangle,
// and pipeline state left bound the way a game leaves it, so a test can check the overlay put it all back.
// Test-only; never shipped.
#define _CRT_SECURE_NO_WARNINGS
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <cstdint>
#include <cstdio>
#include <windows.h>

#define TESTGAME_API extern "C" __declspec(dllexport)

namespace {

struct Game
{
    HWND window = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext1* context = nullptr;
    IDXGISwapChain1* swap_chain = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    ID3D11BlendState* blend = nullptr;
    int width = 0;
    int height = 0;
    bool closed = false;
};

Game* game = nullptr;

// What the "game" leaves bound after drawing: arbitrary values a renderer would change if it did not restore them.
const D3D11_VIEWPORT game_viewport = {7.0f, 11.0f, 123.0f, 45.0f, 0.0f, 1.0f};
const D3D11_PRIMITIVE_TOPOLOGY game_topology = D3D11_PRIMITIVE_TOPOLOGY_POINTLIST;
const float game_blend_factor[4] = {0.25f, 0.5f, 0.75f, 1.0f};
const UINT game_sample_mask = 0x1234;

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w_param, LPARAM l_param)
{
    if (message == WM_CLOSE && game)
    {
        game->closed = true;
        return 0;
    }
    return DefWindowProcW(window, message, w_param, l_param);
}

} // namespace

TESTGAME_API bool TestGame_Create(int32_t width, int32_t height)
{
    if (game)
    {
        return false;
    }
    game = new Game();
    game->width = width;
    game->height = height;

    WNDCLASSW window_class = {};
    window_class.lpfnWndProc = WindowProc;
    window_class.hInstance = GetModuleHandleW(nullptr);
    window_class.lpszClassName = L"RmlOverlayTestGame";
    window_class.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)); // IDC_ARROW, wide
    RegisterClassW(&window_class);
    RECT rect = {0, 0, width, height};
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    game->window = CreateWindowW(L"RmlOverlayTestGame", L"RmlUi overlay test", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, window_class.hInstance, nullptr);
    if (!game->window)
    {
        return false;
    }

    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
    ID3D11DeviceContext* context = nullptr;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, &level, 1, D3D11_SDK_VERSION, &game->device, nullptr, &context)))
    {
        return false;
    }
    context->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&game->context));
    context->Release();

    IDXGIDevice* dxgi_device = nullptr;
    IDXGIAdapter* adapter = nullptr;
    IDXGIFactory2* factory = nullptr;
    game->device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi_device));
    dxgi_device->GetAdapter(&adapter);
    adapter->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory));
    DXGI_SWAP_CHAIN_DESC1 desc = {};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    HRESULT result = factory->CreateSwapChainForHwnd(game->device, game->window, &desc, nullptr, nullptr, &game->swap_chain);
    factory->Release();
    adapter->Release();
    dxgi_device->Release();
    if (FAILED(result))
    {
        return false;
    }

    ID3D11Texture2D* back_buffer = nullptr;
    game->swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back_buffer));
    game->device->CreateRenderTargetView(back_buffer, nullptr, &game->target);
    back_buffer->Release();

    D3D11_BLEND_DESC blend = {};
    blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    game->device->CreateBlendState(&blend, &game->blend);

    ShowWindow(game->window, SW_SHOW);
    return true;
}

TESTGAME_API void* TestGame_GetDevice() { return game ? game->device : nullptr; }
TESTGAME_API void* TestGame_GetSwapChain() { return game ? game->swap_chain : nullptr; }
TESTGAME_API void* TestGame_GetWindow() { return game ? game->window : nullptr; }

/// Dispatches pending window messages. False once the window has been closed.
TESTGAME_API bool TestGame_PumpMessages()
{
    MSG message;
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return game && !game->closed;
}

/// The game's frame: a background colour that changes with time, a rectangle that moves, then the game's state left bound.
TESTGAME_API void TestGame_Draw(float seconds)
{
    float background[4] = {0.1f, 0.2f + 0.2f * static_cast<float>((static_cast<int>(seconds * 60) % 60)) / 60.0f, 0.3f, 1.0f};
    game->context->ClearRenderTargetView(game->target, background);
    int x = static_cast<int>(seconds * 120) % (game->width - 100);
    D3D11_RECT rect = {x, game->height - 140, x + 100, game->height - 40};
    float orange[4] = {1.0f, 0.55f, 0.0f, 1.0f};
    game->context->ClearView(game->target, orange, &rect, 1);

    game->context->OMSetRenderTargets(1, &game->target, nullptr);
    game->context->RSSetViewports(1, &game_viewport);
    game->context->IASetPrimitiveTopology(game_topology);
    game->context->OMSetBlendState(game->blend, game_blend_factor, game_sample_mask);
}

/// Whether the state TestGame_Draw left bound is still bound.
TESTGAME_API bool TestGame_StateIntact()
{
    bool intact = true;

    ID3D11RenderTargetView* target = nullptr;
    game->context->OMGetRenderTargets(1, &target, nullptr);
    intact &= target == game->target;
    if (target)
    {
        target->Release();
    }

    UINT count = 1;
    D3D11_VIEWPORT viewport = {};
    game->context->RSGetViewports(&count, &viewport);
    intact &= count == 1 && viewport.TopLeftX == game_viewport.TopLeftX && viewport.Width == game_viewport.Width;

    D3D11_PRIMITIVE_TOPOLOGY topology = {};
    game->context->IAGetPrimitiveTopology(&topology);
    intact &= topology == game_topology;

    ID3D11BlendState* blend = nullptr;
    float factor[4] = {};
    UINT mask = 0;
    game->context->OMGetBlendState(&blend, factor, &mask);
    intact &= blend == game->blend && factor[1] == game_blend_factor[1] && mask == game_sample_mask;
    if (blend)
    {
        blend->Release();
    }
    return intact;
}

// Copies the back buffer (before presenting) to a staging texture and hands its mapped rows to read.
template <class Read>
bool ReadBackBuffer(Read read)
{
    ID3D11Texture2D* back_buffer = nullptr;
    game->swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back_buffer));
    D3D11_TEXTURE2D_DESC desc = {};
    back_buffer->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    bool ok = SUCCEEDED(game->device->CreateTexture2D(&desc, nullptr, &staging));
    if (ok)
    {
        game->context->CopyResource(staging, back_buffer);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        ok = SUCCEEDED(game->context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped));
        if (ok)
        {
            read(static_cast<const uint8_t*>(mapped.pData), mapped.RowPitch);
            game->context->Unmap(staging, 0);
        }
        staging->Release();
    }
    back_buffer->Release();
    return ok;
}

/// The back buffer's pixel at (x, y), before presenting, as 0xRRGGBBAA.
TESTGAME_API bool TestGame_ReadPixel(int32_t x, int32_t y, uint32_t* rgba)
{
    return ReadBackBuffer([&](const uint8_t* data, UINT pitch)
    {
        const uint8_t* bgra = data + y * pitch + x * 4;
        *rgba = (uint32_t(bgra[2]) << 24) | (uint32_t(bgra[1]) << 16) | (uint32_t(bgra[0]) << 8) | bgra[3];
    });
}

/// The back buffer, before presenting, as a binary PPM.
TESTGAME_API bool TestGame_SaveScreenshot(const char* path)
{
    FILE* file = std::fopen(path, "wb");
    if (!file)
    {
        return false;
    }
    std::fprintf(file, "P6\n%d %d\n255\n", game->width, game->height);
    bool ok = ReadBackBuffer([&](const uint8_t* data, UINT pitch)
    {
        for (int y = 0; y < game->height; y++)
        {
            for (int x = 0; x < game->width; x++)
            {
                const uint8_t* bgra = data + y * pitch + x * 4;
                unsigned char rgb[3] = {bgra[2], bgra[1], bgra[0]};
                std::fwrite(rgb, 1, 3, file);
            }
        }
    });
    std::fclose(file);
    return ok;
}

TESTGAME_API void TestGame_Present()
{
    game->swap_chain->Present(1, 0);
}

TESTGAME_API void TestGame_Destroy()
{
    if (!game)
    {
        return;
    }
    if (game->blend)
    {
        game->blend->Release();
    }
    if (game->target)
    {
        game->target->Release();
    }
    if (game->swap_chain)
    {
        game->swap_chain->Release();
    }
    if (game->context)
    {
        game->context->Release();
    }
    if (game->device)
    {
        game->device->Release();
    }
    if (game->window)
    {
        DestroyWindow(game->window);
    }
    delete game;
    game = nullptr;
}

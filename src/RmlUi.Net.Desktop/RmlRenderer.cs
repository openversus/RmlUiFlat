namespace RmlUi.Desktop;

/// <summary>A renderer for an <see cref="RmlWindow"/>: a preference when building, and the one in use afterwards.</summary>
/// <remarks>Values match RmlBackendRenderer in native/backend/rmlui_c_backend.h.</remarks>
public enum RmlRenderer
{
    /// <summary>Vulkan if this machine can run RmlUi's Vulkan renderer, otherwise OpenGL 3.</summary>
    Auto = 0,

    /// <summary>RmlUi's Vulkan renderer. As a preference, building fails rather than falling back.</summary>
    Vulkan = 1,

    /// <summary>RmlUi's OpenGL 3 renderer. As a preference, Vulkan is not tried.</summary>
    OpenGL = 2,
}

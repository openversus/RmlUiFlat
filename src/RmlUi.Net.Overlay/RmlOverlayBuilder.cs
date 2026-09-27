using RmlUi.Hosting;

namespace RmlUi.Overlay;

/// <summary>Configures and starts an <see cref="RmlOverlay"/>. Fonts and the context name come from <see cref="RmlHostBuilder{TSelf}"/>.</summary>
public sealed class RmlOverlayBuilder : RmlHostBuilder<RmlOverlayBuilder>
{
    private bool visible;

    /// <summary>Whether the overlay draws from the start. Default false: a game menu opens when asked.</summary>
    public RmlOverlayBuilder Visible(bool visible = true)
    {
        this.visible = visible;
        return this;
    }

    /// <summary>
    /// An overlay on a Direct3D 11 device and window you already have, drawn when you call
    /// <see cref="RmlOverlay.RenderFrame"/> from your own render loop.
    /// </summary>
    /// <param name="device">The <c>ID3D11Device*</c> the swap chain belongs to; it must support Direct3D 11.1.</param>
    /// <param name="window">The window (<c>HWND</c>) the swap chain presents to.</param>
    /// <exception cref="InvalidOperationException">The overlay could not start, or another RmlUi host exists.</exception>
    public RmlOverlay BuildForD3D11(nint device, nint window) => new(Settings, device, window, visible);
}

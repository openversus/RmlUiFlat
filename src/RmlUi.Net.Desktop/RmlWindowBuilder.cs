using RmlUi.Hosting;

namespace RmlUi.Desktop;

/// <summary>Configures and opens an <see cref="RmlWindow"/>. Fonts and the context name come from <see cref="RmlHostBuilder{TSelf}"/>.</summary>
public sealed class RmlWindowBuilder : RmlHostBuilder<RmlWindowBuilder>
{
    private readonly string title;
    private readonly int width;
    private readonly int height;
    private bool resizable = true;
    private bool powerSave = true;
    private RmlRenderer renderer = RmlRenderer.Auto;
    private string? appId;
    private byte[]? icon;

    /// <summary>A window with the given title and size.</summary>
    /// <param name="title">The window's title bar text.</param>
    /// <param name="width">
    /// Width in the display's logical units. The context is sized in physical pixels, so on a display scaled to 125%
    /// an 800-wide window has a 1000-wide context; RmlUi's <c>dp</c> unit follows the scale.
    /// </param>
    /// <param name="height">Height in the display's logical units.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is zero or negative.</exception>
    public RmlWindowBuilder(string title, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.title = title;
        this.width = width;
        this.height = height;
    }

    /// <summary>Whether the user can resize the window. Default true.</summary>
    public RmlWindowBuilder Resizable(bool resizable = true)
    {
        this.resizable = resizable;
        return this;
    }

    /// <summary>
    /// Whether each frame waits for input before running. Default true, which idles at no CPU cost; turn it off for
    /// continuous animation or when driving the window from code. Can be changed later on <see cref="RmlWindow.PowerSave"/>.
    /// </summary>
    public RmlWindowBuilder PowerSave(bool powerSave = true)
    {
        this.powerSave = powerSave;
        return this;
    }

    /// <summary>
    /// Which renderer to use. Default <see cref="RmlRenderer.Auto"/>: Vulkan when this machine can run it, OpenGL 3
    /// otherwise. A specific renderer fails the build rather than falling back. <see cref="RmlWindow.Renderer"/> says
    /// which one was chosen.
    /// </summary>
    public RmlWindowBuilder Renderer(RmlRenderer renderer)
    {
        this.renderer = renderer;
        return this;
    }

    /// <summary>
    /// The application id the window is made with, which the desktop pairs with the program's <c>.desktop</c> entry (the
    /// entry's file name without <c>.desktop</c>): Wayland's app_id, X11's WM_CLASS. On Wayland this is also how the window
    /// gets its icon, the entry's. Default: none (SDL uses the program's name).
    /// </summary>
    public RmlWindowBuilder AppId(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        this.appId = appId;
        return this;
    }

    /// <summary>
    /// The window's icon, a PNG or JPEG. Where a window carries an icon of its own (X11, Windows) it is set; where the
    /// desktop supplies it (Wayland, by <see cref="AppId"/> and the desktop entry) nothing happens, and
    /// <see cref="RmlWindow.HasIcon"/> says which. An image that cannot be decoded fails <see cref="Build"/>.
    /// </summary>
    public RmlWindowBuilder Icon(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        icon = image;
        return this;
    }

    /// <summary>Opens the window, initialises RmlUi, loads the fonts and creates the context.</summary>
    /// <exception cref="InvalidOperationException">Another RmlUi host exists, or a step fails; nothing is left initialised.</exception>
    /// <exception cref="ArgumentException">The <see cref="Icon"/> is not an image RmlUi can decode.</exception>
    public RmlWindow Build() => new(Settings, title, width, height, resizable, powerSave, renderer, appId, icon);
}

namespace RmlUi.Desktop;

/// <summary>Configures and opens an <see cref="RmlWindow"/>.</summary>
public sealed class RmlWindowBuilder
{
    private readonly string title;
    private readonly int width;
    private readonly int height;
    private readonly List<RmlWindow.FontSource> fonts = [];
    private bool resizable = true;
    private bool powerSave = true;
    private string contextName = "main";

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

    /// <summary>Name of the RmlUi context the window creates. Default "main".</summary>
    public RmlWindowBuilder ContextName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        contextName = name;
        return this;
    }

    /// <summary>A font file, read by RmlUi when the window is built. Family, style and weight come from the file.</summary>
    public RmlWindowBuilder AddFont(string path, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        fonts.Add(new(path, null, null, Style.FontStyle.Normal, Style.FontWeight.Auto, fallback));
        return this;
    }

    /// <summary>
    /// A font from memory, such as an embedded resource. The bytes are copied; the window keeps its copy in native memory
    /// for as long as RmlUi needs it (until the window is disposed), so the caller's buffer can go away at once.
    /// </summary>
    public RmlWindowBuilder AddFont(ReadOnlySpan<byte> data, string family, Style.FontStyle style = Style.FontStyle.Normal,
        Style.FontWeight weight = Style.FontWeight.Auto, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (data.IsEmpty)
        {
            throw new ArgumentException("Font data is empty.", nameof(data));
        }
        fonts.Add(new(null, data.ToArray(), family, style, weight, fallback));
        return this;
    }

    /// <summary>Opens the window, initialises RmlUi, loads the fonts and creates the context.</summary>
    /// <exception cref="InvalidOperationException">Another window is open, or a step fails; nothing is left initialised.</exception>
    public RmlWindow Build() => new(title, width, height, resizable, powerSave, contextName, fonts);
}

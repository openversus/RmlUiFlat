using System;
using System.Collections.Generic;

namespace RmlUi.Hosting;

/// <summary>
/// The configuration every host takes: fonts and the context name. A host's builder derives from this with itself as
/// <typeparamref name="TSelf"/>, so these calls chain with its own.
/// </summary>
public abstract class RmlHostBuilder<TSelf> where TSelf : RmlHostBuilder<TSelf>
{
    private readonly List<RmlHostSettings.Font> fonts = [];
    private string contextName = "main";

    /// <summary>Name of the RmlUi context the host creates. Default "main".</summary>
    public TSelf ContextName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        contextName = name;
        return (TSelf)this;
    }

    /// <summary>A font file, read by RmlUi when the host is built. Family, style and weight come from the file.</summary>
    public TSelf AddFont(string path, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        fonts.Add(new(path, null, null, Style.FontStyle.Normal, Style.FontWeight.Auto, fallback));
        return (TSelf)this;
    }

    /// <summary>
    /// A font from memory, such as an embedded resource. The bytes are copied; the host keeps its copy in native memory
    /// for as long as RmlUi needs it (until the host is disposed), so the caller's buffer can go away at once.
    /// </summary>
    public TSelf AddFont(ReadOnlySpan<byte> data, string family, Style.FontStyle style = Style.FontStyle.Normal,
        Style.FontWeight weight = Style.FontWeight.Auto, bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (data.IsEmpty)
        {
            throw new ArgumentException("Font data is empty.", nameof(data));
        }
        fonts.Add(new(null, data.ToArray(), family, style, weight, fallback));
        return (TSelf)this;
    }

    /// <summary>The fonts and context name collected so far, for the derived builder to pass to its host.</summary>
    protected RmlHostSettings Settings => new(contextName, fonts.ToArray());
}

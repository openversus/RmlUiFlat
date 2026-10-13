using System;
using System.Collections.Generic;
using System.Reflection;

namespace RmlUi.Hosting;

/// <summary>
/// The configuration every host takes: fonts, asset sources and the context name. A host's builder derives from this with itself as
/// <typeparamref name="TSelf"/>, so these calls chain with its own.
/// </summary>
public abstract class RmlHostBuilder<TSelf> where TSelf : RmlHostBuilder<TSelf>
{
    private readonly List<RmlHostSettings.Font> fonts = [];
    private readonly List<Func<string, byte[]?>> assets = [];
    private string contextName = "main";

    /// <summary>Name of the RmlUi context the host creates. Default "main".</summary>
    public TSelf ContextName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        contextName = name;
        return (TSelf)this;
    }

    /// <summary>
    /// A font file, read by RmlUi when the host is built: through the <see cref="Assets(Func{string, byte[]})"/> sources
    /// when there are any, from the disk otherwise. Family, style and weight come from the file.
    /// </summary>
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

    /// <summary>
    /// Serves the files RmlUi loads by path (documents, style sheets, templates, and fonts and images given by path)
    /// from <paramref name="read"/>, which returns a file's bytes, or null when it has no such file. It is asked for the
    /// path as RmlUi resolved it against the referencing document, with <c>/</c> separators, <c>.</c> and <c>..</c>
    /// collapsed and no leading <c>/</c>: a document <c>ui/main.rml</c> that links <c>main.rcss</c> asks for
    /// <c>ui/main.rcss</c>. Sources are asked in the order they were added, and the first that has the file wins. Once
    /// any source is added, RmlUi reads only through the sources, never from the disk by itself. An exception from
    /// <paramref name="read"/> is reported through <see cref="EventListener.UnhandledException"/> and the file counts as
    /// missing.
    /// </summary>
    public TSelf Assets(Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        assets.Add(read);
        return (TSelf)this;
    }

    /// <summary>
    /// Serves <paramref name="assembly"/>'s embedded resources, each under its logical name read as a path, so a
    /// single-file application carries its documents, style sheets, fonts and images inside it. Give each resource its
    /// path as its logical name in the project file, for example
    /// <c>&lt;EmbeddedResource Include="ui/**" LogicalName="ui/%(RecursiveDir)%(Filename)%(Extension)" /&gt;</c>
    /// (a <c>\</c> in a name counts as <c>/</c>). Otherwise as <see cref="Assets(Func{string, byte[]})"/>.
    /// </summary>
    public TSelf Assets(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        assets.Add(AssetSources.FromAssembly(assembly));
        return (TSelf)this;
    }

    /// <summary>The fonts, asset sources and context name collected so far, for the derived builder to pass to its host.</summary>
    protected RmlHostSettings Settings => new(contextName, fonts.ToArray(), assets.ToArray());
}

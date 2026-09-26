using System.Collections.Generic;

namespace RmlUi.Hosting;

/// <summary>What a <see cref="RmlHostBuilder{TSelf}"/> collected, handed by a derived builder to its host.</summary>
public sealed class RmlHostSettings
{
    internal sealed record Font(string? Path, byte[]? Data, string? Family, Style.FontStyle Style, Style.FontWeight Weight, bool Fallback);

    internal RmlHostSettings(string contextName, IReadOnlyList<Font> fonts)
    {
        ContextName = contextName;
        Fonts = fonts;
    }

    /// <summary>Name of the RmlUi context the host creates.</summary>
    public string ContextName { get; }

    internal IReadOnlyList<Font> Fonts { get; }
}

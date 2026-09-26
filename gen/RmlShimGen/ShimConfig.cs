using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmlShimGen;

/// <summary>
/// shim.json. Paths are relative to the config file's directory.
/// </summary>
public sealed class ShimConfig
{
    /// <summary>RmlUi's public include directory.</summary>
    public string RmlUiInclude { get; set; } = "";

    /// <summary>Header parsed as the translation unit.</summary>
    public string EntryHeader { get; set; } = "RmlUi/Core.h";

    public List<string> SystemIncludeFolders { get; set; } = [];

    public List<string> Defines { get; set; } = [];

    /// <summary>Headers (relative to <see cref="RmlUiInclude"/>) whose free functions in namespace Rml are wrapped.</summary>
    public List<string> FreeFunctionHeaders { get; set; } = [];

    /// <summary>Classes whose public methods are wrapped. Each becomes an opaque handle.</summary>
    public List<string> Classes { get; set; } = [];

    /// <summary>Classes that cross the boundary only as opaque handles; none of their methods are wrapped.</summary>
    public List<string> OpaqueTypes { get; set; } = [];

    /// <summary>Typedefs passed by value as C structs, with layout checked by static_assert in the generated C++.</summary>
    public List<string> ValueTypes { get; set; } = [];

    /// <summary>Qualified C++ names (Rml::Element::Focus) or generated C names to leave out.</summary>
    public List<string> IgnoredFunctions { get; set; } = [];

    /// <summary>Generated C name to final C name.</summary>
    public Dictionary<string, string> Renames { get; set; } = [];

    public string NativeOutput { get; set; } = "";

    public string ReportOutput { get; set; } = "";

    public string HexaGenConfig { get; set; } = "";

    /// <summary>HexaGen's raw bindings over the C API.</summary>
    public string CSharpOutput { get; set; } = "";

    /// <summary>The C# API (RmlUi namespace). The generator owns this directory and recreates it.</summary>
    public string CsApiOutput { get; set; } = "";

    /// <summary>Library name in the C# LibraryImports; DirectPInvoke matches it when statically linked.</summary>
    public string LibraryName { get; set; } = "rmlui_c";

    [JsonIgnore]
    public string BaseDirectory { get; private set; } = "";

    public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));

    public static ShimConfig Load(string path)
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var config = JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"{path} is empty");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return config;
    }
}

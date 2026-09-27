using System.Text.Json;
using System.Text.Json.Serialization;

namespace RmlShimGen;

/// <summary>
/// gen/shim.json: what the generator wraps and where it writes. Paths are relative to the config file's directory.
/// Comments (<c>//</c>) and trailing commas are allowed.
/// </summary>
/// <remarks>
/// Scope is explicit: nothing in RmlUi is wrapped unless a class, header or template is named here. After changing
/// it, regenerate and read the diff of <see cref="ReportOutput"/>, which lists what is still unmapped and why.
/// </remarks>
public sealed class ShimConfig
{
    /// <summary>RmlUi's public include directory (external/RmlUi/Include).</summary>
    public string RmlUiInclude { get; set; } = "";

    /// <summary>The header parsed as the one translation unit, relative to <see cref="RmlUiInclude"/>.</summary>
    public string EntryHeader { get; set; } = "RmlUi/Core.h";

    /// <summary>
    /// The C++ standard library and compiler include directories for the parse (this machine's paths). The parse
    /// target does not change the C ABI: the generated static_asserts check layouts on the compiler that builds the shim.
    /// </summary>
    public List<string> SystemIncludeFolders { get; set; } = [];

    /// <summary>Preprocessor definitions for the parse; RMLUI_STATIC_LIB, as the shim links RmlUi statically.</summary>
    public List<string> Defines { get; set; } = [];

    /// <summary>Headers (relative to <see cref="RmlUiInclude"/>) whose free functions in namespace Rml are wrapped.</summary>
    public List<string> FreeFunctionHeaders { get; set; } = [];

    /// <summary>
    /// Classes whose public methods are wrapped. Each becomes an opaque handle in C and a handle struct in C#, with
    /// upcasts to any base class that is also a handle, and checked downcasts where both declare RmlUi's RTTI.
    /// </summary>
    public List<string> Classes { get; set; } = [];

    /// <summary>Classes that cross only as opaque handles; none of their methods are wrapped.</summary>
    public List<string> OpaqueTypes { get; set; } = [];

    /// <summary>
    /// Typedefs passed by value as C structs (Rml::Vector2f), their fields derived from the template's primitive fields
    /// and their layout checked by static_assert in the generated C++.
    /// </summary>
    public List<string> ValueTypes { get; set; } = [];

    /// <summary>
    /// Functions to leave out, by qualified C++ name (<c>Rml::Element::Focus</c>, every overload) or by generated C name
    /// (<c>RmlElement_GetInnerRML_String</c>, one overload). Listed in the report's count of ignored functions.
    /// </summary>
    public List<string> IgnoredFunctions { get; set; } = [];

    /// <summary>
    /// Function templates to wrap, by qualified C++ name, with the type for their one template parameter:
    /// <c>"Rml::Element::SetAttribute": "Rml::Variant"</c> wraps <c>SetAttribute&lt;Rml::Variant&gt;</c>. A template that
    /// shares its name with other overloads makes them all take overload suffixes, renaming their C functions.
    /// </summary>
    public Dictionary<string, string> TemplateInstantiations { get; set; } = [];

    /// <summary>Generated C name to final C name, for a name that would otherwise clash or mislead.</summary>
    public Dictionary<string, string> Renames { get; set; } = [];

    /// <summary>Where rmlui_c.h and rmlui_c.cpp are written (native/generated).</summary>
    public string NativeOutput { get; set; } = "";

    /// <summary>Where the report is written (gen/shim-report.md): what could not be mapped, by reason, and what is out of scope.</summary>
    public string ReportOutput { get; set; } = "";

    /// <summary>HexaGen's own config for the raw C# layer (gen/hexagen.json). HexaGen rewrites it on load.</summary>
    public string HexaGenConfig { get; set; } = "";

    /// <summary>HexaGen's raw bindings over the C API (src/RmlUi.Net/Generated, namespace RmlUi.Interop).</summary>
    public string CSharpOutput { get; set; } = "";

    /// <summary>The C# API (src/RmlUi.Net/Api.Generated, namespace RmlUi). The generator owns this directory and recreates it.</summary>
    public string CsApiOutput { get; set; } = "";

    /// <summary>Library name in the C# LibraryImports; DirectPInvoke matches it when statically linked.</summary>
    public string LibraryName { get; set; } = "rmlui_c";

    /// <summary>The directory the config was loaded from; relative paths resolve against it.</summary>
    [JsonIgnore]
    public string BaseDirectory { get; private set; } = "";

    /// <summary>A path from the config, made absolute against <see cref="BaseDirectory"/>.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));

    /// <summary>Reads a config file.</summary>
    /// <exception cref="InvalidDataException">The file is empty.</exception>
    public static ShimConfig Load(string path)
    {
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var config = JsonSerializer.Deserialize<ShimConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException($"{path} is empty");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return config;
    }
}

// Generates the RmlUi C shim (native/generated/rmlui_c.h, .cpp) and its C# bindings from RmlUi's headers.
// Usage: RmlShimGen [path/to/shim.json]
using HexaGen.CppAst.Parsing;
using RmlShimGen;

var config = ShimConfig.Load(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../shim.json"));

var include = config.Resolve(config.RmlUiInclude);
var options = new CppParserOptions
{
    ParseMacros = true,
    ParseComments = true,
    ParseSystemIncludes = true,
    ParserKind = CppParserKind.Cpp,
    AutoSquashTypedef = false,
    // CppAst defaults to a 32-bit target, which libstdc++'s headers reject. The shim's C ABI does not depend on this
    // choice: layouts are checked by static_assert when the shim is compiled for the real target.
    TargetCpu = CppTargetCpu.X86_64,
    TargetSystem = "linux",
    TargetVendor = "pc",
    TargetAbi = "gnu",
};
options.IncludeFolders.Add(include);
options.SystemIncludeFolders.AddRange(config.SystemIncludeFolders);
options.Defines.AddRange(config.Defines);
options.AdditionalArguments.Add("-std=c++17");

var compilation = CppParser.ParseFiles([Path.Combine(include, config.EntryHeader)], options);
foreach (var message in compilation.Diagnostics.Messages.Where(m => m.Type == HexaGen.CppAst.Diagnostics.CppLogMessageType.Error))
{
    Console.Error.WriteLine(message);
}
if (compilation.HasErrors)
{
    return 1;
}

new ShimGenerator(config, compilation).Run();

// C# bindings over the generated header and the hand-written one, by HexaGen's C-to-C# generator.
var nativeDir = config.Resolve(config.NativeOutput);
var headers = new List<string> { Path.Combine(nativeDir, "rmlui_c.h"), Path.Combine(nativeDir, "../rmlui_c_manual.h") }.Select(Path.GetFullPath).ToList();
var csConfig = HexaGen.CsCodeGeneratorConfig.Load(config.Resolve(config.HexaGenConfig));
csConfig.IncludeFolders.Add(Path.GetFullPath(Path.Combine(nativeDir, "..")));
// Typed `delegate* unmanaged[Cdecl]<...>` fields rather than void*. The default is true, and a false in the JSON does
// not survive Load, so it is set here.
csConfig.DelegatesAsVoidPointer = false;
var csGenerator = new HexaGen.CsCodeGenerator(csConfig);
csGenerator.LogToConsole();
if (!csGenerator.Generate(headers, config.Resolve(config.CSharpOutput)))
{
    Console.Error.WriteLine("HexaGen failed.");
    return 1;
}
return 0;

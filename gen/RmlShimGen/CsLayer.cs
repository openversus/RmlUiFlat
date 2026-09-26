using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using HexaGen.CppAst.Model.Declarations;

namespace RmlShimGen;

/// <summary>
/// The C# API: one struct per handle with RmlUi's method names, enums and value types as RmlUi spells them, and a
/// static Rml class for the free functions. Calls the C API through its own LibraryImports, so it depends on nothing
/// but the C header's shape.
/// </summary>
public sealed partial class ShimGenerator
{
    private sealed record CsParam(
        string Decl, string CallArg, string NativeDecl, string NativeArg,
        List<string> Pre, string? Fixed, List<string> Post, List<string> Finally, bool CanDefault);

    private sealed record CsReturn(string Type, string NativeType, Func<string, string> Convert);

    private sealed record CsMethod(Wrapped F, string Name, string Key, List<CsParam> Params, List<string?> Defaults, CsReturn Return, bool IsStatic, bool HidesObjectMember)
    {
        /// <summary>Replaces the handle's own member of the same name (object.ToString).</summary>
        public bool IsOverride { get; init; }
    }

    private readonly List<string> csNotes = [];

    private static readonly HashSet<string> CsKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    // Members every handle struct defines itself.
    private static readonly HashSet<string> HandleMembers = ["Handle", "IsNull", "Equals", "GetHashCode", "ToString"];

    private static string CsIdent(string name) => CsKeywords.Contains(name) ? "@" + name : name;

    /// <summary>C++ snake_case to C# camelCase: in_capture_phase becomes inCapturePhase.</summary>
    private static string Camel(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? name : parts[0] + string.Concat(parts.Skip(1).Select(Pascal));
    }

    private static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>Rml::Style::Display becomes Style.Display: RmlUi's namespaces become static classes.</summary>
    private static string CsEnumName(CppEnum e) => e.FullName.StartsWith("Rml::") ? e.FullName[5..].Replace("::", ".") : e.FullName.Replace("::", ".");

    private static string CsPrimitive(string cType) => cType switch
    {
        "bool" => "bool",
        "char" or "uint8_t" => "byte",
        "int8_t" => "sbyte",
        "int16_t" => "short",
        "uint16_t" => "ushort",
        "int32_t" => "int",
        "uint32_t" => "uint",
        "int64_t" => "long",
        "uint64_t" => "ulong",
        "float" => "float",
        "double" => "double",
        "size_t" or "uintptr_t" => "nuint",
        "intptr_t" or "ptrdiff_t" => "nint",
        _ => throw new InvalidOperationException($"no C# type for {cType}"),
    };

    private CsParam MapCsParam(ParamMap p)
    {
        string b = Camel(p.Name);
        string n = CsIdent(b);
        switch (p.Base)
        {
            case StringBase:
                return new($"string {n}", n, $"byte* {n}", $"{b}_m.ToUnmanaged()",
                    [
                        $"scoped Utf8StringMarshaller.ManagedToUnmanagedIn {b}_m = new();",
                        $"{b}_m.FromManaged({n}, stackalloc byte[Utf8StringMarshaller.ManagedToUnmanagedIn.BufferSize]);",
                    ],
                    null, [], [$"{b}_m.Free();"], true);

            case HandleBase h:
                return new($"{ShortName(h.Class.FullName)} {n}", n, $"nint {n}", $"{n}.Handle", [], null, [], [], true);

            case ValueBase v when p.Ind == Indirection.Ref && !p.PointeeConst:
                return new($"ref {ShortName(v.CppName)} {n}", $"ref {n}", $"{ShortName(v.CppName)}* {n}", $"{b}_p", [],
                    $"fixed ({ShortName(v.CppName)}* {b}_p = &{n})", [], [], false);

            case ValueBase v:
                return new($"{ShortName(v.CppName)} {n}", n, $"{ShortName(v.CppName)} {n}", n, [], null, [], [], true);

            case PrimBase { CName: "bool" } when p.Ind == Indirection.Ref && !p.PointeeConst:
                return new($"ref bool {n}", $"ref {n}", $"byte* {n}", $"&{b}_b", [$"byte {b}_b = (byte)({n} ? 1 : 0);"], null,
                    [$"{n} = {b}_b != 0;"], [], false);

            case PrimBase pr when p.Ind == Indirection.Ref && !p.PointeeConst:
                return new($"ref {CsPrimitive(pr.CName)} {n}", $"ref {n}", $"{CsPrimitive(pr.CName)}* {n}", $"{b}_p", [],
                    $"fixed ({CsPrimitive(pr.CName)}* {b}_p = &{n})", [], [], false);

            case PrimBase pr when p.Ind == Indirection.Ptr:
                string pt = pr.CName == "bool" ? "byte" : CsPrimitive(pr.CName);
                return new($"{pt}* {n}", n, $"{pt}* {n}", n, [], null, [], [], true);

            case PrimBase { CName: "bool" }:
                return new($"bool {n}", n, $"byte {n}", $"(byte)({n} ? 1 : 0)", [], null, [], [], true);

            case PrimBase pr:
                return new($"{CsPrimitive(pr.CName)} {n}", n, $"{CsPrimitive(pr.CName)} {n}", n, [], null, [], [], true);

            case VoidBase:
                return new($"void* {n}", n, $"void* {n}", n, [], null, [], [], true);

            case EnumBase e:
                return new($"{CsEnumName(e.Enum)} {n}", n, $"{CsEnumName(e.Enum)} {n}", n, [], null, [], [], true);

            case SpanBase s:
                // RmlUi may keep the pointer (LoadFontFace keeps it until Shutdown), so no managed span that is only pinned for the call.
                string et = CsPrimitive(s.ElementCName);
                return new($"{et}* {n}, nuint {b}Count", $"{n}, {b}Count", $"{et}* {n}, nuint {b}Count", $"{n}, {b}Count",
                    [], null, [], [], false);

            default:
                throw new InvalidOperationException($"no C# mapping for {TypeMapper.Describe(p.Base)}");
        }
    }

    private CsReturn MapCsReturn(ReturnMap r) => r.Base switch
    {
        VoidBase when r.Ind == Indirection.None => new("void", "void", x => x),
        VoidBase => new("void*", "void*", x => x),
        StringBase => new("string", "byte*", x => $"Utf8StringMarshaller.ConvertToManaged({x}) ?? \"\""),
        HandleBase h => new(ShortName(h.Class.FullName), "nint", x => $"new {ShortName(h.Class.FullName)}({x})"),
        ValueBase v => new(ShortName(v.CppName), ShortName(v.CppName), x => x),
        PrimBase { CName: "bool" } when r.Ind == Indirection.Ptr => new("byte*", "byte*", x => x),
        PrimBase pr when r.Ind == Indirection.Ptr => new($"{CsPrimitive(pr.CName)}*", $"{CsPrimitive(pr.CName)}*", x => x),
        PrimBase { CName: "bool" } => new("bool", "byte", x => $"{x} != 0"),
        PrimBase pr => new(CsPrimitive(pr.CName), CsPrimitive(pr.CName), x => x),
        EnumBase e => new(CsEnumName(e.Enum), CsEnumName(e.Enum), x => x),
        _ => throw new InvalidOperationException($"no C# return mapping for {TypeMapper.Describe(r.Base)}"),
    };

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntLiteral();

    [GeneratedRegex(@"^(-?)(\d*)\.?(\d*)(?:[eE](-?\d+))?[fF]?$")]
    private static partial Regex FloatLiteral();

    [GeneratedRegex(@"^[\w:]+\(\s*(?:0(?:\.0*)?f?\s*,?\s*)*\)$|^\{\s*\}$")]
    private static partial Regex ZeroConstruction();

    /// <summary>A C++ default argument as a C# constant, or null when it has no C# spelling.</summary>
    private static string? TranslateDefault(string? cpp, ParamMap p)
    {
        if (cpp == null)
        {
            return null;
        }
        cpp = cpp.Trim();
        switch (p.Base)
        {
            case StringBase:
                if (cpp is "\"\"" or "String()" or "Rml::String()")
                {
                    return "\"\"";
                }
                return cpp.StartsWith('"') && cpp.EndsWith('"') ? cpp : null;

            case HandleBase or VoidBase:
                return cpp is "nullptr" or "NULL" or "0" ? (p.Base is HandleBase ? "default" : "null") : null;

            case PrimBase when p.Ind == Indirection.Ptr:
                return cpp is "nullptr" or "NULL" ? "null" : null;

            case PrimBase { CName: "bool" }:
                return cpp is "true" or "false" ? cpp : null;

            case PrimBase pr when IntLiteral().IsMatch(cpp):
                return pr.CName is "float" ? cpp + "f" : cpp;

            case PrimBase pr when pr.CName is "float" or "double" && FloatLiteral().Match(cpp) is { Success: true } m:
                string whole = m.Groups[2].Value.Length == 0 ? "0" : m.Groups[2].Value;
                string frac = m.Groups[3].Value.Length == 0 ? "0" : m.Groups[3].Value;
                string exp = m.Groups[4].Success ? $"e{m.Groups[4].Value}" : "";
                return $"{m.Groups[1].Value}{whole}.{frac}{exp}{(pr.CName == "float" ? "f" : "")}";

            case EnumBase e when !cpp.Contains('|') && !cpp.Contains('('):
                string item = cpp[(cpp.LastIndexOf(':') + 1)..];
                return e.Enum.Items.Any(i => i.Name == item) ? $"{CsEnumName(e.Enum)}.{item}" : null;

            case ValueBase when ZeroConstruction().IsMatch(cpp):
                return "default";

            default:
                return null;
        }
    }

    private CsMethod? BuildCsMethod(Wrapped f, string name)
    {
        var ps = f.Params.Select(MapCsParam).ToList();
        var defaults = new List<string?>();
        for (int i = 0; i < f.Params.Count; i++)
        {
            defaults.Add(ps[i].CanDefault ? TranslateDefault(f.Function.Parameters[i].InitExpression?.ToString(), f.Params[i]) : null);
        }
        // C# defaults are trailing only: everything up to the last parameter without one is required.
        int lastRequired = defaults.FindLastIndex(d => d == null);
        for (int i = 0; i <= lastRequired; i++)
        {
            defaults[i] = null;
        }

        bool isStatic = f.Owner == null || f.Function.IsStatic;
        string key = $"{name}({string.Join(",", ps.Select(p => Regex.Replace(p.Decl, @"\s+\S+$", "")))})";
        bool hides = f.Owner != null && !isStatic && name == "GetType" && ps.Count == 0;
        return new(f, name, key, ps, defaults, MapCsReturn(f.Return), isStatic, hides);
    }

    /// <summary>C++ names with C# overloading; on a clash, the C name's overload suffix, which is unique by construction.</summary>
    private List<CsMethod> CsMethodsFor(CppClass? owner)
    {
        string prefix = owner == null ? "Rml" : TypeMapper.HandleCName(owner.FullName) + "_";
        string? typeName = owner == null ? null : ShortName(owner.FullName);
        var methods = new List<CsMethod>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in wrapped.Where(w => w.Owner == owner))
        {
            string name = f.Function.Name;
            // A C++ String ToString() const is what a C# reader expects ToString() to return, so it replaces the
            // handle's own, which only prints the address.
            if (owner != null && name == "ToString" && f.Params.Count == 0 && f.Return.Base is StringBase && !f.Function.IsStatic
                && BuildCsMethod(f, name) is { } toString && keys.Add(toString.Key))
            {
                methods.Add(toString with { IsOverride = true });
                continue;
            }
            string? why = name == typeName ? "same name as its type"
                : owner != null && HandleMembers.Contains(name) ? "same name as a handle member"
                : null;
            var m = why == null ? BuildCsMethod(f, name) : null;
            if (m == null || !keys.Add(m.Key))
            {
                why ??= "same C# signature as another overload";
                string fallback = f.CName.StartsWith(prefix, StringComparison.Ordinal) ? f.CName[prefix.Length..] : f.CName;
                fallback = string.Concat(fallback.Split('_').Select(Pascal));
                m = BuildCsMethod(f, fallback);
                if (m == null || !keys.Add(m.Key))
                {
                    csNotes.Add($"`{f.CppName}`: {why}, and the fallback name {fallback} clashes too; not in the C# API");
                    continue;
                }
                csNotes.Add($"`{f.CppName}`: {why}; C# name {fallback}");
            }
            methods.Add(m);
        }
        return methods;
    }

    private void WriteCsLayer()
    {
        var dir = config.Resolve(config.CsApiOutput);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(dir);

        var free = CsMethodsFor(null);
        var perClass = config.Classes.ToDictionary(c => c, c => CsMethodsFor(classes[c]));
        var upcasts = Upcasts().ToList();
        var downcasts = Downcasts().ToList();

        File.WriteAllText(Path.Combine(dir, "NativeMethods.cs"), WriteNativeMethods(free, perClass, upcasts, downcasts));
        File.WriteAllText(Path.Combine(dir, "Rml.cs"), Wrap(w =>
        {
            w.AppendLine("/// <summary>RmlUi's free functions (namespace Rml).</summary>");
            w.AppendLine("public static unsafe partial class Rml");
            w.AppendLine("{");
            foreach (var m in free)
            {
                WriteCsMethod(w, m);
            }
            w.AppendLine("}");
        }));

        foreach (var name in config.Classes.Concat(config.OpaqueTypes))
        {
            string shortName = ShortName(name);
            File.WriteAllText(Path.Combine(dir, $"{shortName}.cs"), Wrap(w =>
            {
                bool ownToString = perClass.TryGetValue(name, out var own0) && own0.Any(m => m.IsOverride);
                WriteHandleStart(w, name, shortName, ownToString);
                foreach (var (from, to) in upcasts.Where(u => u.From == name))
                {
                    w.AppendLine($"    /// <summary>Upcast to {to}.</summary>");
                    w.AppendLine($"    public static implicit operator {ShortName(to)}({shortName} value) => new(NativeMethods.{TypeMapper.HandleCName(from)}_As{ShortName(to)}(value.Handle));");
                    w.AppendLine();
                }
                // A named method rather than an explicit operator: a C# cast is expected to throw, and this returns null.
                foreach (var (_, @base) in downcasts.Where(d => d.Derived == name))
                {
                    w.AppendLine($"    /// <summary>The {ShortName(@base)} as a {shortName}, or a null handle when it is not one.</summary>");
                    w.AppendLine($"    public static {shortName} From({ShortName(@base)} value) => new(NativeMethods.{TypeMapper.HandleCName(name)}_From{ShortName(@base)}(value.Handle));");
                    w.AppendLine();
                }
                if (perClass.TryGetValue(name, out var methods))
                {
                    foreach (var m in methods)
                    {
                        WriteCsMethod(w, m);
                    }
                    // C# does not look through implicit conversions for methods, so inherited ones are forwarded.
                    var own = new HashSet<string>(methods.Select(m => m.Key));
                    foreach (var (from, to) in upcasts.Where(u => u.From == name && perClass.ContainsKey(u.To)))
                    {
                        foreach (var m in perClass[to].Where(m => !m.IsStatic && !m.IsOverride && own.Add(m.Key)))
                        {
                            WriteCsForwarder(w, m, ShortName(to));
                        }
                    }
                }
                w.AppendLine("}");
            }));
        }

        File.WriteAllText(Path.Combine(dir, "Enums.cs"), Wrap(WriteCsEnums));
        File.WriteAllText(Path.Combine(dir, "ValueTypes.cs"), Wrap(WriteCsValueTypes));
    }

    private static string Wrap(Action<StringBuilder> body)
    {
        var w = new StringBuilder();
        w.AppendLine("// Generated by gen/RmlShimGen. Do not edit; change gen/shim.json and regenerate.");
        w.AppendLine("#nullable enable");
        w.AppendLine("using System;");
        w.AppendLine("using System.Runtime.CompilerServices;");
        w.AppendLine("using System.Runtime.InteropServices;");
        w.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        w.AppendLine();
        w.AppendLine("namespace RmlUi;");
        w.AppendLine();
        body(w);
        return w.ToString();
    }

    private static void WriteHandleStart(StringBuilder w, string qualified, string shortName, bool ownToString)
    {
        w.AppendLine($"/// <summary>A {qualified}*. Does not own it.</summary>");
        w.AppendLine($"public readonly unsafe partial struct {shortName} : IEquatable<{shortName}>");
        w.AppendLine("{");
        w.AppendLine($"    /// <summary>The native {qualified}*.</summary>");
        w.AppendLine("    public readonly nint Handle;");
        w.AppendLine();
        w.AppendLine($"    public {shortName}(nint handle) => Handle = handle;");
        w.AppendLine();
        w.AppendLine("    public bool IsNull => Handle == 0;");
        w.AppendLine();
        w.AppendLine($"    public bool Equals({shortName} other) => Handle == other.Handle;");
        w.AppendLine();
        w.AppendLine($"    public override bool Equals(object? obj) => obj is {shortName} other && Equals(other);");
        w.AppendLine();
        w.AppendLine("    public override int GetHashCode() => Handle.GetHashCode();");
        w.AppendLine();
        if (!ownToString)
        {
            w.AppendLine($"    public override string ToString() => $\"{qualified}(0x{{Handle:X}})\";");
            w.AppendLine();
        }
        w.AppendLine($"    public static bool operator ==({shortName} a, {shortName} b) => a.Handle == b.Handle;");
        w.AppendLine();
        w.AppendLine($"    public static bool operator !=({shortName} a, {shortName} b) => a.Handle != b.Handle;");
        w.AppendLine();
    }

    private static void WriteCsDoc(StringBuilder w, Wrapped f, string indent)
    {
        var comment = f.Function.Comment?.ToString()?.Trim();
        w.AppendLine($"{indent}/// <summary>");
        if (!string.IsNullOrEmpty(comment))
        {
            foreach (var line in comment.Split('\n'))
            {
                w.AppendLine($"{indent}/// {SecurityElement.Escape(line.TrimEnd())}<br/>".Replace("/// <br/>", "/// "));
            }
        }
        w.AppendLine($"{indent}/// </summary>");
        w.AppendLine($"{indent}/// <remarks>C++: <c>{SecurityElement.Escape(Signature(f.Function, f.Owner))}</c></remarks>");
    }

    private static string DeclList(CsMethod m) =>
        string.Join(", ", m.Params.Select((p, i) => m.Defaults[i] is { } d ? $"{p.Decl} = {d}" : p.Decl));

    private static void WriteCsMethod(StringBuilder w, CsMethod m)
    {
        WriteCsDoc(w, m.F, "    ");
        string modifiers = "public" + (m.IsStatic ? " static" : "") + (m.HidesObjectMember ? " new" : "") + (m.IsOverride ? " override" : "");
        w.AppendLine($"    {modifiers} {m.Return.Type} {m.Name}({DeclList(m)})");
        w.AppendLine("    {");
        if (m.IsOverride)
        {
            // Interpolation and debuggers call ToString on anything, null handles included.
            w.AppendLine("        if (IsNull)");
            w.AppendLine("        {");
            w.AppendLine($"            return \"{m.F.Owner!.FullName}(null)\";");
            w.AppendLine("        }");
        }

        var args = new List<string>();
        if (!m.IsStatic)
        {
            args.Add("Handle");
        }
        args.AddRange(m.Params.Select(p => p.NativeArg));
        string call = $"NativeMethods.{m.F.CName}({string.Join(", ", args)})";

        string indent = "        ";
        foreach (var line in m.Params.SelectMany(p => p.Pre))
        {
            w.AppendLine($"{indent}{line}");
        }
        var cleanup = m.Params.SelectMany(p => p.Finally).ToList();
        if (cleanup.Count > 0)
        {
            w.AppendLine($"{indent}try");
            w.AppendLine($"{indent}{{");
            indent += "    ";
        }
        var fixeds = m.Params.Where(p => p.Fixed != null).Select(p => p.Fixed!).ToList();
        foreach (var fx in fixeds)
        {
            w.AppendLine($"{indent}{fx}");
        }
        if (fixeds.Count > 0)
        {
            w.AppendLine($"{indent}{{");
            indent += "    ";
        }

        var post = m.Params.SelectMany(p => p.Post).ToList();
        if (m.Return.Type == "void")
        {
            w.AppendLine($"{indent}{call};");
            foreach (var line in post)
            {
                w.AppendLine($"{indent}{line}");
            }
        }
        else if (post.Count == 0)
        {
            w.AppendLine($"{indent}return {m.Return.Convert(call)};");
        }
        else
        {
            w.AppendLine($"{indent}var ret = {call};");
            foreach (var line in post)
            {
                w.AppendLine($"{indent}{line}");
            }
            w.AppendLine($"{indent}return {m.Return.Convert("ret")};");
        }

        if (fixeds.Count > 0)
        {
            indent = indent[..^4];
            w.AppendLine($"{indent}}}");
        }
        if (cleanup.Count > 0)
        {
            indent = indent[..^4];
            w.AppendLine($"{indent}}}");
            w.AppendLine($"{indent}finally");
            w.AppendLine($"{indent}{{");
            foreach (var line in cleanup)
            {
                w.AppendLine($"{indent}    {line}");
            }
            w.AppendLine($"{indent}}}");
        }
        w.AppendLine("    }");
        w.AppendLine();
    }

    private static void WriteCsForwarder(StringBuilder w, CsMethod m, string baseName)
    {
        WriteCsDoc(w, m.F, "    ");
        string modifiers = "public" + (m.HidesObjectMember ? " new" : "");
        string args = string.Join(", ", m.Params.Select(p => p.CallArg));
        w.AppendLine($"    {modifiers} {m.Return.Type} {m.Name}({DeclList(m)}) => (({baseName})this).{m.Name}({args});");
        w.AppendLine();
    }

    private string WriteNativeMethods(
        List<CsMethod> free, Dictionary<string, List<CsMethod>> perClass, List<(string From, string To)> upcasts, List<(string Derived, string Base)> downcasts)
    {
        return Wrap(w =>
        {
            w.AppendLine("/// <summary>The C API in native/generated/rmlui_c.h.</summary>");
            w.AppendLine("internal static unsafe partial class NativeMethods");
            w.AppendLine("{");
            w.AppendLine($"    internal const string LibraryName = \"{config.LibraryName}\";");
            w.AppendLine();
            foreach (var (from, to) in upcasts)
            {
                string entry = $"{TypeMapper.HandleCName(from)}_As{ShortName(to)}";
                WriteImport(w, "nint", entry, "nint self");
            }
            foreach (var (derived, @base) in downcasts)
            {
                WriteImport(w, "nint", $"{TypeMapper.HandleCName(derived)}_From{ShortName(@base)}", "nint value");
            }
            foreach (var m in free.Concat(config.Classes.SelectMany(c => perClass[c])))
            {
                var decls = new List<string>();
                if (!m.IsStatic)
                {
                    decls.Add("nint self");
                }
                decls.AddRange(m.Params.Select(p => p.NativeDecl));
                WriteImport(w, m.Return.NativeType, m.F.CName, string.Join(", ", decls));
            }
            w.AppendLine("}");
        });
    }

    private static void WriteImport(StringBuilder w, string ret, string entry, string decls)
    {
        w.AppendLine("    [LibraryImport(LibraryName)]");
        w.AppendLine("    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]");
        w.AppendLine($"    internal static partial {ret} {entry}({decls});");
        w.AppendLine();
    }

    private void WriteCsEnums(StringBuilder w)
    {
        // Group by containing namespace path so Style.* share one static class.
        foreach (var group in mapper.UsedEnums.Values.GroupBy(e => CsEnumName(e).Contains('.') ? CsEnumName(e)[..CsEnumName(e).LastIndexOf('.')] : ""))
        {
            var containers = group.Key.Length == 0 ? [] : group.Key.Split('.');
            var handles = new HashSet<string>(config.Classes.Concat(config.OpaqueTypes));
            string indent = "";
            for (int i = 0; i < containers.Length; i++)
            {
                string c = containers[i];
                // An enum declared inside a wrapped class (Rml::ElementScroll::Orientation) nests in its handle struct.
                bool isHandle = handles.Contains("Rml::" + string.Join("::", containers[..(i + 1)]));
                w.AppendLine(isHandle ? $"{indent}public readonly unsafe partial struct {c}" : $"{indent}public static partial class {c}");
                w.AppendLine($"{indent}{{");
                indent += "    ";
            }
            foreach (var e in group)
            {
                string name = CsEnumName(e);
                name = name[(name.LastIndexOf('.') + 1)..];
                w.AppendLine($"{indent}/// <summary>{e.FullName}</summary>");
                w.AppendLine($"{indent}public enum {name} : int");
                w.AppendLine($"{indent}{{");
                foreach (var item in e.Items)
                {
                    w.AppendLine($"{indent}    {CsIdent(item.Name)} = {EnumValue(e, item)},");
                }
                w.AppendLine($"{indent}}}");
                w.AppendLine();
            }
            foreach (var _ in containers)
            {
                indent = indent[..^4];
                w.AppendLine($"{indent}}}");
            }
            w.AppendLine();
        }
    }

    private void WriteCsValueTypes(StringBuilder w)
    {
        foreach (var (_, fields, cppName) in valueStructs)
        {
            string name = ShortName(cppName);
            var cs = fields.Select(f => (Type: CsPrimitive(f.Type), Name: Pascal(f.Name), Param: CsIdent(f.Name))).ToList();
            w.AppendLine($"/// <summary>{cppName}, laid out as RmlUi's (checked by static_assert in the C shim).</summary>");
            w.AppendLine("[StructLayout(LayoutKind.Sequential)]");
            w.AppendLine($"public partial struct {name} : IEquatable<{name}>");
            w.AppendLine("{");
            foreach (var f in cs)
            {
                w.AppendLine($"    public {f.Type} {f.Name};");
            }
            w.AppendLine();
            w.AppendLine($"    public {name}({string.Join(", ", cs.Select(f => $"{f.Type} {f.Param}"))})");
            w.AppendLine("    {");
            foreach (var f in cs)
            {
                w.AppendLine($"        {f.Name} = {f.Param};");
            }
            w.AppendLine("    }");
            w.AppendLine();
            w.AppendLine($"    public readonly bool Equals({name} other) => {string.Join(" && ", cs.Select(f => $"{f.Name} == other.{f.Name}"))};");
            w.AppendLine();
            w.AppendLine($"    public override readonly bool Equals(object? obj) => obj is {name} other && Equals(other);");
            w.AppendLine();
            w.AppendLine($"    public override readonly int GetHashCode() => HashCode.Combine({string.Join(", ", cs.Select(f => f.Name))});");
            w.AppendLine();
            w.AppendLine($"    public override readonly string ToString() => $\"({string.Join(", ", cs.Select(f => $"{{{f.Name}}}"))})\";");
            w.AppendLine();
            w.AppendLine($"    public static bool operator ==({name} a, {name} b) => a.Equals(b);");
            w.AppendLine();
            w.AppendLine($"    public static bool operator !=({name} a, {name} b) => !a.Equals(b);");
            w.AppendLine("}");
            w.AppendLine();
        }
    }
}

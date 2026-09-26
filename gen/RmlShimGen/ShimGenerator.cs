using System.Text;
using System.Text.RegularExpressions;
using HexaGen.CppAst.Model;
using HexaGen.CppAst.Model.Declarations;
using HexaGen.CppAst.Model.Metadata;
using HexaGen.CppAst.Model.Templates;
using HexaGen.CppAst.Model.Types;

namespace RmlShimGen;

/// <summary>Walks the configured slice of RmlUi and writes rmlui_c.h, rmlui_c.cpp and the report.</summary>
public sealed partial class ShimGenerator(ShimConfig config, CppCompilation compilation)
{
    private sealed record Wrapped(string CName, string CppName, CppFunction Function, CppClass? Owner, ReturnMap Return, List<ParamMap> Params);

    private sealed record Unmapped(string CppName, string Reason);

    private readonly TypeMapper mapper = new(config);
    private readonly List<Wrapped> wrapped = [];
    private readonly List<Unmapped> unmapped = [];
    private readonly List<string> ignored = [];
    private readonly Dictionary<string, CppClass> classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CppTypedef> typedefs = new(StringComparer.Ordinal);
    private readonly List<(string CName, List<(string Type, string Name)> Fields, string CppName)> valueStructs = [];
    private readonly List<string> valueFailures = [];

    public int UnmappedCount => unmapped.Count;

    public void Run()
    {
        Index(compilation.Namespaces);
        foreach (var name in config.Classes.Concat(config.OpaqueTypes))
        {
            if (!classes.ContainsKey(name))
            {
                throw new InvalidDataException($"shim.json names {name}, which is not defined in the parsed headers.");
            }
        }

        BuildValueStructs();

        foreach (var header in config.FreeFunctionHeaders)
        {
            var path = Path.Combine(config.Resolve(config.RmlUiInclude), header);
            var functions = FindNamespace("Rml")?.Functions.Where(f => SameFile(f.SourceFile, path)) ?? [];
            WrapGroup(functions.ToList(), owner: null);
        }

        foreach (var name in config.Classes)
        {
            var cls = classes[name];
            var methods = cls.Functions.Where(IsWrappable).ToList();
            WrapGroup(methods, cls);
        }

        var nativeDir = config.Resolve(config.NativeOutput);
        Directory.CreateDirectory(nativeDir);
        File.WriteAllText(Path.Combine(nativeDir, "rmlui_c.h"), WriteHeader());
        File.WriteAllText(Path.Combine(nativeDir, "rmlui_c.cpp"), WriteSource());
        File.WriteAllText(config.Resolve(config.ReportOutput), WriteReport());

        Console.WriteLine($"wrapped {wrapped.Count} functions, {unmapped.Count} unmapped, {ignored.Count} ignored, {mapper.UsedEnums.Count} enums, {valueStructs.Count} value types");
    }

    private static bool SameFile(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private void Index(IEnumerable<CppNamespace> namespaces)
    {
        foreach (var ns in namespaces)
        {
            foreach (var cls in ns.Classes)
            {
                // Forward declarations appear too; keep the definition.
                if (cls.IsDefinition || !classes.ContainsKey(cls.FullName))
                {
                    classes[cls.FullName] = cls;
                }
            }
            foreach (var td in ns.Typedefs)
            {
                typedefs[td.FullName] = td;
            }
            Index(ns.Namespaces);
        }
    }

    private CppNamespace? FindNamespace(string name) => compilation.Namespaces.FirstOrDefault(n => n.Name == name);

    private static bool IsWrappable(CppFunction f) =>
        f.Visibility == CppVisibility.Public
        && !f.IsConstructor
        && !f.IsDestructor
        && !f.IsFunctionTemplate
        && !f.Name.StartsWith("operator", StringComparison.Ordinal)
        && (f.Flags & CppFunctionFlags.Deleted) == 0;

    private static string QualifiedName(CppFunction f, CppClass? owner) => owner == null ? $"Rml::{f.Name}" : $"{owner.FullName}::{f.Name}";

    /// <summary>
    /// Wraps one class's (or one header's) functions. Overloads are named from their C++ parameter types, counting
    /// every overload rather than only the mapped ones, so adding a mapping rule later never renames a function.
    /// </summary>
    private void WrapGroup(List<CppFunction> functions, CppClass? owner)
    {
        var overloadCounts = functions.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.Count());
        var taken = new HashSet<string>(StringComparer.Ordinal);
        string prefix = owner == null ? "Rml" : TypeMapper.HandleCName(owner.FullName) + "_";

        // Non-const first, so a const/non-const pair keeps the non-const one.
        foreach (var f in functions.OrderBy(f => f.IsConst))
        {
            string cppName = QualifiedName(f, owner);
            string cName = prefix + f.Name;
            if (overloadCounts[f.Name] > 1)
            {
                cName += OverloadSuffix(f);
            }
            if (config.Renames.TryGetValue(cName, out var renamed))
            {
                cName = renamed;
            }

            if (config.IgnoredFunctions.Contains(cppName) || config.IgnoredFunctions.Contains(cName))
            {
                ignored.Add(cName);
                continue;
            }
            if ((f.Flags & CppFunctionFlags.Variadic) != 0)
            {
                unmapped.Add(new(Signature(f, owner), "variadic"));
                continue;
            }
            if (!taken.Add(cName))
            {
                unmapped.Add(new(Signature(f, owner), $"const overload of {cName}"));
                continue;
            }

            try
            {
                var ret = mapper.MapReturn(f.ReturnType);
                var ps = new List<ParamMap>();
                for (int i = 0; i < f.Parameters.Count; i++)
                {
                    var p = f.Parameters[i];
                    ps.Add(mapper.MapParam(p.Type, ParamName(p, i)));
                }
                wrapped.Add(new(cName, cppName, f, owner, ret, ps));
            }
            catch (UnmappedException e)
            {
                unmapped.Add(new(Signature(f, owner), e.Message));
            }
        }
    }

    // Names that would collide with the shim's own locals, or with C, get a suffix.
    private static readonly HashSet<string> ReservedNames = ["self", "rml_ret", "rml_string_result"];

    private static string ParamName(CppParameter p, int index)
    {
        if (string.IsNullOrEmpty(p.Name))
        {
            return $"arg{index}";
        }
        return ReservedNames.Contains(p.Name) ? p.Name + "_" : p.Name;
    }

    private static string OverloadSuffix(CppFunction f)
    {
        if (f.Parameters.Count == 0)
        {
            return "";
        }
        var parts = f.Parameters.Select(p => TypeWord(TypeMapper.Spell(p.Type)));
        return "_" + string.Join("_", parts);
    }

    private static string TypeWord(string cppType)
    {
        var s = cppType.Replace("Rml::", "").Replace("const ", "").Replace("&", "").Replace("*", "Ptr");
        s = s.Split('<')[0];
        s = NonIdentifier().Replace(s, "");
        return s.Length == 0 ? "T" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonIdentifier();

    private static string Signature(CppFunction f, CppClass? owner)
    {
        var ps = f.Parameters.Select(p =>
        {
            var s = $"{TypeMapper.Spell(p.Type)} {p.Name}".TrimEnd();
            return p.InitExpression != null ? $"{s} = {p.InitExpression}" : s;
        });
        return $"{TypeMapper.Spell(f.ReturnType)} {QualifiedName(f, owner)}({string.Join(", ", ps)}){(f.IsConst ? " const" : "")}";
    }

    /// <summary>
    /// Derives each value type's C fields from its template's fields, substituting the template arguments.
    /// Only primitive fields are derived; the generated static_asserts check the result against the real type.
    /// </summary>
    private void BuildValueStructs()
    {
        foreach (var name in config.ValueTypes)
        {
            try
            {
                if (!typedefs.TryGetValue(name, out var td))
                {
                    throw new UnmappedException("typedef not found");
                }
                var cls = td.ElementType as CppClass ?? throw new UnmappedException($"not a class ({td.ElementType.GetType().Name})");
                var template = cls.SpecializedTemplate ?? cls;
                var fields = new List<(string, string)>();
                foreach (var field in template.Fields.Where(f => (f.StorageQualifier & CppStorageQualifier.Static) == 0))
                {
                    var type = SubstituteTemplateArgument(field.Type, template, cls);
                    var map = mapper.MapParam(type, field.Name);
                    if (map.CDecls.Count != 1 || map.CppArg != field.Name)
                    {
                        throw new UnmappedException($"field {field.Name} is not primitive");
                    }
                    fields.Add((map.CDecls[0][..^(field.Name.Length + 1)], field.Name));
                }
                if (fields.Count == 0)
                {
                    throw new UnmappedException("no fields found");
                }
                valueStructs.Add((TypeMapper.ValueCName(name), fields, name));
            }
            catch (UnmappedException e)
            {
                valueFailures.Add($"{name}: {e.Message}");
            }
        }
        if (valueFailures.Count > 0)
        {
            throw new InvalidDataException("Value types could not be derived:\n  " + string.Join("\n  ", valueFailures));
        }
    }

    private static CppType SubstituteTemplateArgument(CppType type, CppClass template, CppClass specialization)
    {
        string? name = type switch
        {
            CppTemplateParameterType tp => tp.Name,
            CppUnexposedType u => u.Name,
            _ => null,
        };
        if (name == null)
        {
            return type;
        }
        for (int i = 0; i < template.TemplateParameters.Count && i < specialization.TemplateSpecializedArguments.Count; i++)
        {
            if (template.TemplateParameters[i] is CppTemplateParameterType p && p.Name == name)
            {
                return specialization.TemplateSpecializedArguments[i].ArgAsType
                    ?? throw new UnmappedException($"template argument {name} is not a type");
            }
        }
        throw new UnmappedException($"unresolved template type {name}");
    }

    private string WriteHeader()
    {
        var w = new StringBuilder();
        w.AppendLine("""
            /* Generated by gen/RmlShimGen from RmlUi's headers. Do not edit; change gen/shim.json and regenerate. */
            #ifndef RMLUI_C_H
            #define RMLUI_C_H

            #include <stdbool.h>
            #include <stddef.h>
            #include <stdint.h>

            #if defined(RMLC_STATIC)
            #define RMLC_API
            #elif defined(_WIN32)
            #if defined(RMLC_BUILD)
            #define RMLC_API __declspec(dllexport)
            #else
            #define RMLC_API __declspec(dllimport)
            #endif
            #else
            #define RMLC_API __attribute__((visibility("default")))
            #endif

            #ifdef __cplusplus
            extern "C" {
            #endif

            """);

        w.AppendLine("/* Handles */");
        foreach (var name in config.Classes.Concat(config.OpaqueTypes))
        {
            var c = TypeMapper.HandleCName(name);
            w.AppendLine($"typedef struct {c} {c};");
        }
        w.AppendLine();

        w.AppendLine("/* Value types */");
        foreach (var (cName, fields, cppName) in valueStructs)
        {
            w.AppendLine($"/** {cppName} */");
            w.AppendLine($"typedef struct {cName}");
            w.AppendLine("{");
            foreach (var (type, field) in fields)
            {
                w.AppendLine($"    {type} {field};");
            }
            w.AppendLine($"}} {cName};");
            w.AppendLine();
        }

        w.AppendLine("/* Enums */");
        foreach (var e in mapper.UsedEnums.Values)
        {
            var cName = TypeMapper.EnumCName(e);
            w.AppendLine($"/** {e.FullName} */");
            w.AppendLine($"typedef enum {cName}");
            w.AppendLine("{");
            foreach (var item in e.Items)
            {
                w.AppendLine($"    {cName}_{item.Name} = {EnumValue(e, item)},");
            }
            w.AppendLine($"}} {cName};");
            w.AppendLine();
        }

        w.AppendLine("/* Upcasts */");
        foreach (var (from, to) in Upcasts())
        {
            w.AppendLine($"RMLC_API {TypeMapper.HandleCName(to)}* {TypeMapper.HandleCName(from)}_As{ShortName(to)}({TypeMapper.HandleCName(from)}* self);");
        }
        w.AppendLine();

        CppClass? current = null;
        foreach (var f in wrapped)
        {
            if (f.Owner != current || (current == null && f == wrapped[0]))
            {
                current = f.Owner;
                w.AppendLine($"/* {(current == null ? "namespace Rml" : current.FullName)} */");
            }
            WriteDocComment(w, f);
            w.AppendLine($"RMLC_API {f.Return.CType} {f.CName}({CParams(f)});");
        }

        w.AppendLine("""

            #ifdef __cplusplus
            }
            #endif

            #endif
            """);
        return w.ToString();
    }

    private static void WriteDocComment(StringBuilder w, Wrapped f)
    {
        w.AppendLine("/**");
        var comment = f.Function.Comment?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(comment))
        {
            foreach (var line in comment.Split('\n'))
            {
                w.AppendLine($" * {line.TrimEnd().Replace("*/", "* /")}".TrimEnd());
            }
        }
        w.AppendLine($" * C++: {Signature(f.Function, f.Owner).Replace("*/", "* /")}");
        if (f.Return.CType == "const char*")
        {
            w.AppendLine(f.Return.Statements.Count > 1
                ? " * The returned string is valid until the next call on this thread that returns a string by value."
                : " * The returned string is owned by RmlUi and valid while its owner is.");
        }
        w.AppendLine(" */");
    }

    private static string CParams(Wrapped f)
    {
        var ps = new List<string>();
        if (f.Owner != null && !f.Function.IsStatic)
        {
            ps.Add($"{TypeMapper.HandleCName(f.Owner.FullName)}* self");
        }
        ps.AddRange(f.Params.SelectMany(p => p.CDecls));
        return ps.Count == 0 ? "void" : string.Join(", ", ps);
    }

    private static string EnumValue(CppEnum e, CppEnumItem item)
    {
        long value = item.Value;
        // CppAst sign-extends: an unsigned 8-bit 0xff arrives as -1. Mask to the underlying width.
        var underlying = e.IntegerType;
        while (underlying is CppTypedef td)
        {
            underlying = td.ElementType;
        }
        if (value < 0 && underlying is CppPrimitiveType { Kind: CppPrimitiveKind.UnsignedChar or CppPrimitiveKind.UnsignedShort or CppPrimitiveKind.UnsignedInt } p)
        {
            value &= (1L << (int)(p.SizeOf * 8)) - 1;
        }
        return value.ToString();
    }

    private static string ShortName(string qualified) => qualified[(qualified.LastIndexOf(':') + 1)..];

    /// <summary>Every base class of a wrapped class that is itself a handle, direct or not.</summary>
    private IEnumerable<(string From, string To)> Upcasts()
    {
        var handles = new HashSet<string>(config.Classes.Concat(config.OpaqueTypes));
        foreach (var name in config.Classes)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<CppClass>([classes[name]]);
            while (stack.Count > 0)
            {
                foreach (var b in stack.Pop().BaseTypes)
                {
                    if (b.Type is CppClass bc && seen.Add(bc.FullName))
                    {
                        if (handles.Contains(bc.FullName))
                        {
                            yield return (name, bc.FullName);
                        }
                        stack.Push(classes.GetValueOrDefault(bc.FullName) ?? bc);
                    }
                }
            }
        }
    }

    private string WriteSource()
    {
        var w = new StringBuilder();
        w.AppendLine("""
            // Generated by gen/RmlShimGen from RmlUi's headers. Do not edit; change gen/shim.json and regenerate.
            #define RMLC_BUILD
            #include "rmlui_c.h"

            #include <RmlUi/Core.h>
            #include <bit>
            #include <cstddef>
            #include <type_traits>
            #include <utility>

            // Value types are copied with std::bit_cast; these hold the C layout to the C++ one on the compiler that builds this.
            """);
        foreach (var (cName, fields, cppName) in valueStructs)
        {
            w.AppendLine($"static_assert(std::is_trivially_copyable_v<{cppName}> && std::is_standard_layout_v<{cppName}>, \"{cppName} cannot be bit_cast\");");
            w.AppendLine($"static_assert(sizeof({cName}) == sizeof({cppName}) && alignof({cName}) == alignof({cppName}), \"{cName} size differs from {cppName}\");");
            foreach (var (_, field) in fields)
            {
                w.AppendLine($"static_assert(offsetof({cName}, {field}) == offsetof({cppName}, {field}), \"{cName}.{field} offset differs\");");
            }
        }
        w.AppendLine();

        w.AppendLine("// Enum values are written as numbers; these hold them to RmlUi's.");
        foreach (var e in mapper.UsedEnums.Values)
        {
            var cName = TypeMapper.EnumCName(e);
            foreach (var item in e.Items)
            {
                w.AppendLine($"static_assert(static_cast<long long>({cName}_{item.Name}) == static_cast<long long>({e.FullName}::{item.Name}));");
            }
        }
        w.AppendLine();

        foreach (var (from, to) in Upcasts())
        {
            string cFrom = TypeMapper.HandleCName(from), cTo = TypeMapper.HandleCName(to);
            w.AppendLine($"RMLC_API {cTo}* {cFrom}_As{ShortName(to)}({cFrom}* self)");
            w.AppendLine("{");
            w.AppendLine($"    return reinterpret_cast<{cTo}*>(static_cast<{to}*>(reinterpret_cast<{from}*>(self)));");
            w.AppendLine("}");
            w.AppendLine();
        }

        foreach (var f in wrapped)
        {
            w.AppendLine($"RMLC_API {f.Return.CType} {f.CName}({CParams(f)})");
            w.AppendLine("{");
            foreach (var p in f.Params)
            {
                foreach (var line in p.Pre)
                {
                    w.AppendLine($"    {line}");
                }
            }
            string args = string.Join(", ", f.Params.Select(p => p.CppArg));
            string call = f.Owner == null ? $"{f.CppName}({args})"
                : f.Function.IsStatic ? $"{f.CppName}({args})"
                : $"reinterpret_cast<{f.Owner.FullName}*>(self)->{f.Function.Name}({args})";
            if (f.Return.IsVoid)
            {
                w.AppendLine($"    {call};");
            }
            else
            {
                w.AppendLine($"    decltype(auto) rml_ret = {call};");
            }
            foreach (var p in f.Params)
            {
                foreach (var line in p.Post)
                {
                    w.AppendLine($"    {line}");
                }
            }
            foreach (var line in f.Return.Statements)
            {
                w.AppendLine($"    {line}");
            }
            w.AppendLine("}");
            w.AppendLine();
        }
        return w.ToString();
    }

    private string WriteReport()
    {
        var w = new StringBuilder();
        w.AppendLine("# RmlUi shim report");
        w.AppendLine();
        w.AppendLine("Generated by gen/RmlShimGen. Diff this file after an RmlUi update: it lists what changed shape.");
        w.AppendLine();
        w.AppendLine($"Wrapped {wrapped.Count} functions; {unmapped.Count} in scope could not be mapped; {ignored.Count} ignored by shim.json.");
        w.AppendLine();

        w.AppendLine("## In scope, not mapped");
        w.AppendLine();
        foreach (var group in unmapped.GroupBy(u => u.Reason).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            w.AppendLine($"### {group.Key} ({group.Count()})");
            w.AppendLine();
            foreach (var u in group.OrderBy(u => u.CppName, StringComparer.Ordinal))
            {
                w.AppendLine($"- `{u.CppName}`");
            }
            w.AppendLine();
        }

        w.AppendLine("## In RmlUi's headers, not in scope");
        w.AppendLine();
        w.AppendLine("Public classes with public methods that shim.json does not list in Classes.");
        w.AppendLine();
        var include = config.Resolve(config.RmlUiInclude);
        var inScope = new HashSet<string>(config.Classes);
        foreach (var cls in classes.Values
            .Where(c => c.IsDefinition && c.SourceFile != null && Path.GetFullPath(c.SourceFile).StartsWith(include, StringComparison.Ordinal))
            .Where(c => c.TemplateKind == CppTemplateKind.NormalClass && !inScope.Contains(c.FullName))
            .Where(c => c.Functions.Any(IsWrappable))
            .OrderBy(c => c.FullName, StringComparer.Ordinal))
        {
            w.AppendLine($"- `{cls.FullName}` ({cls.Functions.Count(IsWrappable)} methods)");
        }
        return w.ToString();
    }
}

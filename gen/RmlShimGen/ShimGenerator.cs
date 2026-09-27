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
    private sealed record Wrapped(string CName, string CppName, CppFunction Function, CppClass? Owner, ReturnMap Return, List<ParamMap> Params)
    {
        /// <summary>For an instantiated function template (shim.json TemplateInstantiations), its template argument.</summary>
        public string? TemplateArgument { get; init; }
    }

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
            var methods = cls.Functions
                .Where(f => (IsWrappable(f) || IsInstantiated(f, cls)) && !(DeclaresRtti(cls) && RttiMethods.Contains(f.Name)))
                .ToList();
            WrapGroup(methods, cls);
        }

        var nativeDir = config.Resolve(config.NativeOutput);
        Directory.CreateDirectory(nativeDir);
        File.WriteAllText(Path.Combine(nativeDir, "rmlui_c.h"), WriteHeader());
        File.WriteAllText(Path.Combine(nativeDir, "rmlui_c.cpp"), WriteSource());
        WriteCsLayer();
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

    private static bool IsWrappable(CppFunction f) => IsCallable(f) && !f.IsFunctionTemplate;

    /// <summary>A function template shim.json lists in TemplateInstantiations.</summary>
    private bool IsInstantiated(CppFunction f, CppClass? owner) =>
        IsCallable(f) && f.IsFunctionTemplate && config.TemplateInstantiations.ContainsKey(QualifiedName(f, owner));

    private static bool IsCallable(CppFunction f) =>
        f.Visibility == CppVisibility.Public
        && !f.IsConstructor
        && !f.IsDestructor
        && !f.Name.StartsWith("operator", StringComparison.Ordinal)
        && (f.Flags & CppFunctionFlags.Deleted) == 0;

    // RmlUi's own RTTI (RMLUI_RTTI_DefineWithParent). The generated From downcasts use it; the ClassId values these
    // return mean nothing to a caller, so they are not wrapped.
    private static readonly HashSet<string> RttiMethods = ["GetClassIdentifier", "GetStaticClassIdentifier", "IsClass"];

    private static bool DeclaresRtti(CppClass c) => c.Functions.Any(f => f.Name == "GetStaticClassIdentifier");

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
                string? templateArgument = f.IsFunctionTemplate ? config.TemplateInstantiations[cppName] : null;
                var instantiate = templateArgument == null ? (Func<CppType, CppType>)(t => t) : InstantiateTemplate(f, templateArgument);
                var ret = mapper.MapReturn(instantiate(f.ReturnType));
                var outs = DocumentedOut(f, owner);
                var ps = new List<ParamMap>();
                for (int i = 0; i < f.Parameters.Count; i++)
                {
                    var p = f.Parameters[i];
                    ps.Add(mapper.MapParam(instantiate(p.Type), ParamName(p, i), outs.Contains(p.Name)));
                }
                wrapped.Add(new(cName, cppName, f, owner, ret, ps) { TemplateArgument = templateArgument });
            }
            catch (UnmappedException e)
            {
                unmapped.Add(new(Signature(f, owner), e.Message));
            }
        }
    }

    /// <summary>Parameters RmlUi's doc comment marks <c>@param[out]</c>: written by the callee, never read.</summary>
    private HashSet<string> DocumentedOut(CppFunction f, CppClass? owner)
    {
        var result = DocumentedOut(f);
        // An override often has no comment of its own; the one it overrides documents its parameters.
        for (var cls = owner; result.Count == 0 && cls != null;)
        {
            cls = cls.BaseTypes.Select(b => b.Type as CppClass).FirstOrDefault(b => b != null) is { } b0 ? classes.GetValueOrDefault(b0.FullName) ?? b0 : null;
            var overridden = cls?.Functions.FirstOrDefault(g => g.Name == f.Name && g.Parameters.Count == f.Parameters.Count);
            if (overridden != null)
            {
                // Parameter names can differ between the declarations; the position is what carries over.
                var names = DocumentedOut(overridden);
                result = [.. f.Parameters.Where((p, i) => names.Contains(overridden.Parameters[i].Name)).Select(p => p.Name)];
                break;
            }
        }
        return result;
    }

    private static HashSet<string> DocumentedOut(CppFunction f)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<CppComment>();
        if (f.Comment != null)
        {
            pending.Push(f.Comment);
        }
        while (pending.Count > 0)
        {
            var comment = pending.Pop();
            if (comment is CppCommentParamCommand { IsDirectionExplicit: true, Direction: CppCommentParamDirection.Out } param)
            {
                result.Add(param.ParamName);
            }
            foreach (var child in comment.Children ?? [])
            {
                pending.Push(child);
            }
        }
        return result;
    }

    // Names that would collide with the shim's own locals, or with C, get a suffix.
    private static readonly HashSet<string> ReservedNames = ["self", "rml_ret", "rml_string_result", "rml_list", "rml_i", "rml_item"];

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
                    var map = mapper.MapParam(type, field.Name, documentedOut: false);
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

    /// <summary>Replaces a function template's one type parameter with <paramref name="argument"/> wherever it appears.</summary>
    private Func<CppType, CppType> InstantiateTemplate(CppFunction f, string argument)
    {
        if (f.TemplateParameters.Count != 1 || f.TemplateParameters[0] is not CppTemplateParameterType parameter)
        {
            throw new UnmappedException("an instantiation needs exactly one type template parameter");
        }
        CppType type = classes.GetValueOrDefault(argument) as CppType ?? typedefs.GetValueOrDefault(argument)
            ?? throw new InvalidDataException($"TemplateInstantiations names {argument}, which is not a class or typedef in the parsed headers.");
        CppType Substitute(CppType t) => t switch
        {
            CppTemplateParameterType p when p.Name == parameter.Name => type,
            CppUnexposedType u when u.Name == parameter.Name => type,
            // clang leaves "const T" unexposed inside a function template.
            CppUnexposedType u when u.Name == "const " + parameter.Name => new CppQualifiedType(default, default, CppTypeQualifier.Const, type),
            // The clang cursor and type are only for CppAst's own lookups; the mapper reads the element type and qualifier.
            CppQualifiedType q => new CppQualifiedType(default, default, q.Qualifier, Substitute(q.ElementType)),
            CppReferenceType r => new CppReferenceType(default, default, Substitute(r.ElementType)),
            CppPointerType p => new CppPointerType(default, default, Substitute(p.ElementType)),
            _ => t,
        };
        return Substitute;
    }

    // Variant crosses as its exact stored value, read with GetReference: no TypeConverter runs. RmlUi's converters have
    // gaps (a BYTE or CHAR converts to almost nothing) and a gap asserts in debug builds, so the C# side converts instead.
    private const string VariantHelpers = """
        [[maybe_unused]] static RmlVariant rmlc_variant_out(const Rml::Variant* v)
        {
            RmlVariant out{};
            if (!v)
                return out;
            out.type = static_cast<RmlVariantType>(v->GetType());
            switch (v->GetType())
            {
            case Rml::Variant::BOOL: out.integer = v->GetReference<bool>(); break;
            case Rml::Variant::BYTE: out.integer = v->GetReference<Rml::byte>(); break;
            case Rml::Variant::CHAR: out.integer = v->GetReference<char>(); break;
            case Rml::Variant::INT: out.integer = v->GetReference<int>(); break;
            case Rml::Variant::INT64: out.integer = v->GetReference<int64_t>(); break;
            case Rml::Variant::UINT: out.integer = v->GetReference<unsigned int>(); break;
            case Rml::Variant::UINT64: out.integer = static_cast<int64_t>(v->GetReference<uint64_t>()); break;
            case Rml::Variant::FLOAT: out.real = v->GetReference<float>(); break;
            case Rml::Variant::DOUBLE: out.real = v->GetReference<double>(); break;
            case Rml::Variant::STRING: out.string = v->GetReference<Rml::String>().c_str(); break;
            case Rml::Variant::VECTOR2: { const auto& x = v->GetReference<Rml::Vector2f>(); out.vector[0] = x.x; out.vector[1] = x.y; break; }
            case Rml::Variant::VECTOR3: { const auto& x = v->GetReference<Rml::Vector3f>(); out.vector[0] = x.x; out.vector[1] = x.y; out.vector[2] = x.z; break; }
            case Rml::Variant::VECTOR4: { const auto& x = v->GetReference<Rml::Vector4f>(); out.vector[0] = x.x; out.vector[1] = x.y; out.vector[2] = x.z; out.vector[3] = x.w; break; }
            case Rml::Variant::COLOURF: { const auto& x = v->GetReference<Rml::Colourf>(); out.vector[0] = x.red; out.vector[1] = x.green; out.vector[2] = x.blue; out.vector[3] = x.alpha; break; }
            case Rml::Variant::COLOURB: { const auto& x = v->GetReference<Rml::Colourb>(); out.vector[0] = x.red; out.vector[1] = x.green; out.vector[2] = x.blue; out.vector[3] = x.alpha; break; }
            default: break;
            }
            return out;
        }

        [[maybe_unused]] static Rml::Variant rmlc_variant_in(const RmlVariant& v)
        {
            switch (static_cast<Rml::Variant::Type>(v.type))
            {
            case Rml::Variant::BOOL: return Rml::Variant(v.integer != 0);
            case Rml::Variant::BYTE: return Rml::Variant(static_cast<Rml::byte>(v.integer));
            case Rml::Variant::CHAR: return Rml::Variant(static_cast<char>(v.integer));
            case Rml::Variant::INT: return Rml::Variant(static_cast<int>(v.integer));
            case Rml::Variant::INT64: return Rml::Variant(static_cast<int64_t>(v.integer));
            case Rml::Variant::UINT: return Rml::Variant(static_cast<unsigned int>(v.integer));
            case Rml::Variant::UINT64: return Rml::Variant(static_cast<uint64_t>(v.integer));
            case Rml::Variant::FLOAT: return Rml::Variant(static_cast<float>(v.real));
            case Rml::Variant::DOUBLE: return Rml::Variant(v.real);
            case Rml::Variant::STRING: return Rml::Variant(Rml::String(v.string ? v.string : ""));
            case Rml::Variant::VECTOR2: return Rml::Variant(Rml::Vector2f(v.vector[0], v.vector[1]));
            case Rml::Variant::VECTOR3: return Rml::Variant(Rml::Vector3f(v.vector[0], v.vector[1], v.vector[2]));
            case Rml::Variant::VECTOR4: return Rml::Variant(Rml::Vector4f(v.vector[0], v.vector[1], v.vector[2], v.vector[3]));
            case Rml::Variant::COLOURF: return Rml::Variant(Rml::Colourf(v.vector[0], v.vector[1], v.vector[2], v.vector[3]));
            case Rml::Variant::COLOURB:
                return Rml::Variant(Rml::Colourb(static_cast<Rml::byte>(v.vector[0]), static_cast<Rml::byte>(v.vector[1]),
                    static_cast<Rml::byte>(v.vector[2]), static_cast<Rml::byte>(v.vector[3])));
            default: return Rml::Variant();
            }
        }

        [[maybe_unused]] static Rml::Dictionary rmlc_dictionary_in(const RmlDictionary& d)
        {
            Rml::Dictionary result;
            for (size_t i = 0; i < d.count; i++)
                result[Rml::String(d.data[i].key ? d.data[i].key : "")] = rmlc_variant_in(d.data[i].value);
            return result;
        }

        [[maybe_unused]] static RmlDictionary rmlc_dictionary_out(const Rml::Dictionary& d, Rml::Vector<RmlDictionaryEntry>& entries)
        {
            entries.clear();
            for (const auto& [key, value] : d)
                entries.push_back(RmlDictionaryEntry{ key.c_str(), rmlc_variant_out(&value) });
            return RmlDictionary{ entries.data(), entries.size() };
        }

        """;

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

        w.AppendLine("/* Lists */");
        foreach (var (cName, itemType) in mapper.UsedLists)
        {
            // Handle items are void* because HexaGen's raw layer cannot wrap a handle** field (it names a type it never
            // generates); the C# API reads them as nint either way.
            string dataType = itemType == "const char*" ? "const char* const*" : "void* const*";
            w.AppendLine($"/** A list of {itemType}: points into a per-thread copy, valid until the same function is next called on the thread. */");
            w.AppendLine($"typedef struct {cName}");
            w.AppendLine("{");
            w.AppendLine($"    {dataType} data;");
            w.AppendLine("    size_t count;");
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

        if (mapper.VariantType is { } variantType)
        {
            string typeName = TypeMapper.EnumCName(variantType);
            w.AppendLine($$"""
                /* Variant and Dictionary */
                /**
                 * An Rml::Variant, copied: its exact stored value, unconverted. type 0 means no variant (a null Variant*);
                 * otherwise the field for its type holds the value, and pointer and list types carry their type only.
                 */
                typedef struct RmlVariant
                {
                    {{typeName}} type;
                    int64_t integer;     /* BOOL, BYTE, CHAR, INT, INT64, UINT; UINT64 as its bits */
                    double real;         /* FLOAT, DOUBLE */
                    float vector[4];     /* VECTOR2, VECTOR3, VECTOR4, COLOURF by component; COLOURB as 0-255 */
                    const char* string;  /* STRING: valid until the variant's owner changes, or the call's per-thread copy is reused */
                } RmlVariant;

                typedef struct RmlDictionaryEntry
                {
                    const char* key;
                    RmlVariant value;
                } RmlDictionaryEntry;

                /** A Dictionary (ElementAttributes): points into a per-thread copy, valid until the same function is next called on the thread. */
                typedef struct RmlDictionary
                {
                    const RmlDictionaryEntry* data;
                    size_t count;
                } RmlDictionary;

                """);
        }

        w.AppendLine("/* Upcasts */");
        foreach (var (from, to) in Upcasts())
        {
            w.AppendLine($"RMLC_API {TypeMapper.HandleCName(to)}* {TypeMapper.HandleCName(from)}_As{ShortName(to)}({TypeMapper.HandleCName(from)}* self);");
        }
        w.AppendLine();

        w.AppendLine("/* Owned objects (RmlUi's ElementPtr): release one that was never handed back to RmlUi */");
        foreach (var owned in mapper.UsedOwned)
        {
            w.AppendLine($"RMLC_API void {TypeMapper.ReleaseOwnedCName(owned)}({TypeMapper.HandleCName(owned)}* value);");
        }
        w.AppendLine();

        w.AppendLine("/* Checked downcasts: null when the object is not of that type */");
        foreach (var (derived, @base) in Downcasts())
        {
            w.AppendLine($"RMLC_API {TypeMapper.HandleCName(derived)}* {TypeMapper.HandleCName(derived)}_From{ShortName(@base)}({TypeMapper.HandleCName(@base)}* value);");
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

    /// <summary>The upcasts that can be checked the other way: both classes declare RmlUi's RTTI.</summary>
    private IEnumerable<(string Derived, string Base)> Downcasts() =>
        Upcasts().Where(u => DeclaresRtti(classes[u.From]) && classes.GetValueOrDefault(u.To) is { } b && DeclaresRtti(b));

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

        if (mapper.VariantType != null)
        {
            w.AppendLine(VariantHelpers);
        }

        foreach (var owned in mapper.UsedOwned)
        {
            w.AppendLine($"RMLC_API void {TypeMapper.ReleaseOwnedCName(owned)}({TypeMapper.HandleCName(owned)}* value)");
            w.AppendLine("{");
            w.AppendLine($"    Rml::UniqueReleaserPtr<{owned}> owned(reinterpret_cast<{owned}*>(value));");
            w.AppendLine("}");
            w.AppendLine();
        }

        foreach (var (derived, @base) in Downcasts())
        {
            string cDerived = TypeMapper.HandleCName(derived), cBase = TypeMapper.HandleCName(@base);
            w.AppendLine($"RMLC_API {cDerived}* {cDerived}_From{ShortName(@base)}({cBase}* value)");
            w.AppendLine("{");
            w.AppendLine($"    return reinterpret_cast<{cDerived}*>(::rmlui_dynamic_cast<{derived}*>(reinterpret_cast<{@base}*>(value)));");
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
            string templateArgs = f.TemplateArgument == null ? "" : $"<{f.TemplateArgument}>";
            string call = f.Owner == null ? $"{f.CppName}{templateArgs}({args})"
                : f.Function.IsStatic ? $"{f.CppName}{templateArgs}({args})"
                : $"reinterpret_cast<{f.Owner.FullName}*>(self)->{f.Function.Name}{templateArgs}({args})";
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

        w.AppendLine("## C# names that differ from C++");
        w.AppendLine();
        w.AppendLine("Methods whose RmlUi name could not be used as is in the C# API (src/RmlUi.Net/Api.Generated).");
        w.AppendLine();
        foreach (var note in csNotes)
        {
            w.AppendLine($"- {note}");
        }
        w.AppendLine();

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

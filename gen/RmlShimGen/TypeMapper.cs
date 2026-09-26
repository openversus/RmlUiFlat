using HexaGen.CppAst.Model.Declarations;
using HexaGen.CppAst.Model.Types;

namespace RmlShimGen;

/// <summary>How one C++ parameter crosses: its C declarations, and the C++ that rebuilds the argument.</summary>
public sealed record ParamMap(List<string> CDecls, string CppArg, List<string> Pre, List<string> Post);

/// <summary>How a C++ return value crosses: its C type, and the statements that return it given the value in <c>rml_ret</c>.</summary>
public sealed record ReturnMap(string CType, bool IsVoid, List<string> Statements);

/// <summary>Thrown (and caught per function) when a type has no rule; the message goes into the report.</summary>
public sealed class UnmappedException(string reason) : Exception(reason);

public sealed class TypeMapper
{
    private enum Indirection { None, Ref, Ptr }

    private abstract record Base;
    private sealed record VoidBase : Base;
    private sealed record StringBase : Base;
    private sealed record PrimBase(string CName, string? Widen) : Base;
    private sealed record HandleBase(CppClass Class) : Base;
    private sealed record ValueBase(string CppName, string CName) : Base;
    private sealed record EnumBase(CppEnum Enum) : Base;
    private sealed record SpanBase(string ElementCName) : Base;

    // Typedefs that C spells the same way; kept by name so size_t stays size_t on both sides.
    private static readonly HashSet<string> CTypedefs =
    [
        "size_t", "ptrdiff_t", "intptr_t", "uintptr_t",
        "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    ];

    private readonly HashSet<string> handleClasses;
    private readonly HashSet<string> valueTypes;

    public TypeMapper(ShimConfig config)
    {
        handleClasses = [.. config.Classes, .. config.OpaqueTypes];
        valueTypes = [.. config.ValueTypes];
    }

    /// <summary>Enums reached by any mapped signature, keyed by qualified name.</summary>
    public SortedDictionary<string, CppEnum> UsedEnums { get; } = new(StringComparer.Ordinal);

    public static string HandleCName(string qualified) => "Rml" + StripRml(qualified).Replace("::", "");

    public static string EnumCName(CppEnum e) => "Rml" + StripRml(e.FullName).Replace("::", "");

    public static string ValueCName(string qualified) => "Rml" + StripRml(qualified).Replace("::", "");

    private static string StripRml(string qualified) => qualified.StartsWith("Rml::") ? qualified[5..] : qualified;

    public ParamMap MapParam(CppType type, string name)
    {
        var (b, ind, pointeeConst) = Classify(type);
        string c = pointeeConst ? "const " : "";
        switch (b)
        {
            case StringBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"const char* {name}"], $"Rml::String({name} ? {name} : \"\")", [], []);

            case StringBase when ind == Indirection.Ref:
                throw new UnmappedException("String& out-parameter");

            case HandleBase h when ind == Indirection.Ptr:
                return new([$"{c}{HandleCName(h.Class.FullName)}* {name}"], $"reinterpret_cast<{c}{h.Class.FullName}*>({name})", [], []);

            case HandleBase h when ind == Indirection.Ref:
                return new([$"{c}{HandleCName(h.Class.FullName)}* {name}"], $"*reinterpret_cast<{c}{h.Class.FullName}*>({name})", [], []);

            case ValueBase v when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"{v.CName} {name}"], $"std::bit_cast<{v.CppName}>({name})", [], []);

            case ValueBase v when ind == Indirection.Ref:
                // Out-parameter: copy through a real object rather than aliasing the C struct as the C++ type.
                return new(
                    [$"{v.CName}* {name}"],
                    $"{name}_tmp",
                    [$"{v.CppName} {name}_tmp = std::bit_cast<{v.CppName}>(*{name});"],
                    [$"*{name} = std::bit_cast<{v.CName}>({name}_tmp);"]);

            case PrimBase p when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"{p.CName} {name}"], p.Widen == null ? name : $"static_cast<{p.Widen}>({name})", [], []);

            case PrimBase p when ind == Indirection.Ref && p.Widen == null:
                return new([$"{p.CName}* {name}"], $"*{name}", [], []);

            case PrimBase p when ind == Indirection.Ptr && p.Widen == null:
                return new([$"{c}{p.CName}* {name}"], name, [], []);

            case VoidBase when ind == Indirection.Ptr:
                return new([$"{c}void* {name}"], name, [], []);

            case EnumBase e when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                UsedEnums[e.Enum.FullName] = e.Enum;
                return new([$"{EnumCName(e.Enum)} {name}"], $"static_cast<{e.Enum.FullName}>({name})", [], []);

            case SpanBase s when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"const {s.ElementCName}* {name}", $"size_t {name}_count"], $"Rml::Span<const {s.ElementCName}>({name}, {name}_count)", [], []);

            default:
                throw new UnmappedException($"{Describe(b)} {ind}{(pointeeConst ? " const" : "")} parameter");
        }
    }

    public ReturnMap MapReturn(CppType type)
    {
        var (b, ind, pointeeConst) = Classify(type);
        string c = pointeeConst ? "const " : "";
        switch (b)
        {
            case VoidBase when ind == Indirection.None:
                return new("void", true, []);

            case VoidBase when ind == Indirection.Ptr:
                return new($"{c}void*", false, ["return rml_ret;"]);

            case StringBase when ind == Indirection.Ref:
                // Refers to a string the object owns; valid while the object is.
                return new("const char*", false, ["return rml_ret.c_str();"]);

            case StringBase when ind == Indirection.None:
                // A temporary: park it per thread. Valid until the next string-by-value call on this thread;
                // HexaGen's string overloads copy it before returning.
                return new("const char*", false,
                [
                    "thread_local Rml::String rml_string_result;",
                    "rml_string_result = std::move(rml_ret);",
                    "return rml_string_result.c_str();",
                ]);

            case HandleBase h when ind == Indirection.Ptr:
                return new($"{c}{HandleCName(h.Class.FullName)}*", false, [$"return reinterpret_cast<{c}{HandleCName(h.Class.FullName)}*>(rml_ret);"]);

            case HandleBase h when ind == Indirection.Ref:
                return new($"{c}{HandleCName(h.Class.FullName)}*", false, [$"return reinterpret_cast<{c}{HandleCName(h.Class.FullName)}*>(&rml_ret);"]);

            case ValueBase v when ind != Indirection.Ptr:
                return new(v.CName, false, [$"return std::bit_cast<{v.CName}>(rml_ret);"]);

            case PrimBase p when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new(p.CName, false, [p.Widen == null ? "return rml_ret;" : $"return static_cast<{p.CName}>(rml_ret);"]);

            case PrimBase p when ind == Indirection.Ptr && p.Widen == null:
                return new($"{c}{p.CName}*", false, ["return rml_ret;"]);

            case EnumBase e when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                UsedEnums[e.Enum.FullName] = e.Enum;
                return new(EnumCName(e.Enum), false, [$"return static_cast<{EnumCName(e.Enum)}>(rml_ret);"]);

            default:
                throw new UnmappedException($"{Describe(b)} {ind}{(pointeeConst ? " const" : "")} return");
        }
    }

    private static string Describe(Base b) => b switch
    {
        VoidBase => "void",
        StringBase => "String",
        PrimBase p => p.CName,
        HandleBase h => h.Class.FullName,
        ValueBase v => v.CppName,
        EnumBase e => e.Enum.FullName,
        SpanBase s => $"Span<{s.ElementCName}>",
        _ => b.ToString(),
    };

    /// <summary>The type as RmlUi's headers spell it: typedef names kept, no elaborated enum bodies.</summary>
    public static string Spell(CppType type) => type switch
    {
        CppQualifiedType { Qualifier: CppTypeQualifier.Const } q => $"const {Spell(q.ElementType)}",
        CppQualifiedType q => Spell(q.ElementType),
        CppReferenceType r => $"{Spell(r.ElementType)}&",
        CppPointerType p => $"{Spell(p.ElementType)}*",
        CppTypedef td => td.Name,
        CppEnum e => e.Name,
        CppClass c when c.TemplateSpecializedArguments.Count > 0 =>
            $"{c.Name}<{string.Join(", ", c.TemplateSpecializedArguments.Select(a => a.ArgAsType is { } t ? Spell(t) : a.ArgString))}>",
        CppClass c => c.Name,
        _ => type.ToString(),
    };

    /// <summary>Splits a type into one level of reference or pointer and the base type under it.</summary>
    private (Base, Indirection, bool pointeeConst) Classify(CppType type)
    {
        bool pointeeConst = false;
        Indirection ind = Indirection.None;

        type = StripQualifiers(type, out _);
        type = ResolveTransparentTypedefs(type);
        if (type is CppReferenceType r)
        {
            ind = Indirection.Ref;
            type = r.ElementType;
        }
        else if (type is CppPointerType p)
        {
            ind = Indirection.Ptr;
            type = p.ElementType;
        }

        if (ind != Indirection.None)
        {
            type = StripQualifiers(type, out pointeeConst);
            var inner = ResolveTransparentTypedefs(type);
            if (inner is CppPointerType or CppReferenceType)
            {
                throw new UnmappedException($"multi-level indirection ({type.FullName})");
            }
        }

        return (ClassifyBase(type), ind, pointeeConst);
    }

    private static CppType StripQualifiers(CppType type, out bool isConst)
    {
        isConst = false;
        while (type is CppQualifiedType q)
        {
            isConst |= q.Qualifier == CppTypeQualifier.Const;
            type = q.ElementType;
        }
        return type;
    }

    /// <summary>Looks through typedefs that are only a name for a pointer or reference, so the indirection is seen.</summary>
    private CppType ResolveTransparentTypedefs(CppType type)
    {
        while (type is CppTypedef td && !IsNamedTypedef(td) && StripQualifiers(td.ElementType, out _) is CppTypedef or CppPointerType or CppReferenceType)
        {
            type = StripQualifiers(td.ElementType, out _);
        }
        return type;
    }

    private bool IsNamedTypedef(CppTypedef td) =>
        td.FullName == "Rml::String" || valueTypes.Contains(td.FullName) || CTypedefs.Contains(td.Name);

    private Base ClassifyBase(CppType type)
    {
        string? viaTypedef = null;
        while (true)
        {
            type = StripQualifiers(type, out _);
            switch (type)
            {
                case CppTypedef td when td.FullName == "Rml::String":
                    return new StringBase();
                case CppTypedef td when valueTypes.Contains(td.FullName):
                    return new ValueBase(td.FullName, ValueCName(td.FullName));
                case CppTypedef td when CTypedefs.Contains(td.Name):
                    return new PrimBase(td.Name, null);
                case CppTypedef td:
                    viaTypedef ??= td.FullName;
                    type = td.ElementType;
                    continue;
                case CppPrimitiveType p:
                    return MapPrimitive(p);
                case CppEnum e when !e.IsAnonymous:
                    return new EnumBase(e);
                case CppClass cls when handleClasses.Contains(cls.FullName):
                    return new HandleBase(cls);
                case CppClass cls when cls.FullName.StartsWith("Rml::Span<", StringComparison.Ordinal):
                    return MapSpan(cls);
                case CppClass cls:
                    throw new UnmappedException(viaTypedef ?? cls.FullName);
                default:
                    throw new UnmappedException($"{type.GetType().Name} {viaTypedef ?? type.FullName}");
            }
        }
    }

    private SpanBase MapSpan(CppClass span)
    {
        var arg = span.TemplateSpecializedArguments.FirstOrDefault()?.ArgAsType
            ?? throw new UnmappedException($"{span.FullName} (no template argument exposed)");
        arg = StripQualifiers(arg, out bool isConst);
        if (!isConst)
        {
            throw new UnmappedException($"mutable Span<{arg.FullName}>");
        }
        var element = ClassifyBase(arg);
        if (element is PrimBase { Widen: null } p)
        {
            return new SpanBase(p.CName);
        }
        throw new UnmappedException($"Span<const {Describe(element)}>");
    }

    private static Base MapPrimitive(CppPrimitiveType p) => p.Kind switch
    {
        CppPrimitiveKind.Void => new VoidBase(),
        CppPrimitiveKind.Bool => new PrimBase("bool", null),
        CppPrimitiveKind.Char => new PrimBase("char", null),
        CppPrimitiveKind.UnsignedChar => new PrimBase("uint8_t", null),
        CppPrimitiveKind.Short => new PrimBase("int16_t", null),
        CppPrimitiveKind.UnsignedShort => new PrimBase("uint16_t", null),
        CppPrimitiveKind.Int => new PrimBase("int32_t", null),
        CppPrimitiveKind.UnsignedInt => new PrimBase("uint32_t", null),
        // long is 32-bit on Windows and 64-bit on Linux; the ABI carries 64 and the shim converts.
        CppPrimitiveKind.Long => new PrimBase("int64_t", "long"),
        CppPrimitiveKind.UnsignedLong => new PrimBase("uint64_t", "unsigned long"),
        CppPrimitiveKind.LongLong => new PrimBase("int64_t", null),
        CppPrimitiveKind.UnsignedLongLong => new PrimBase("uint64_t", null),
        CppPrimitiveKind.Float => new PrimBase("float", null),
        CppPrimitiveKind.Double => new PrimBase("double", null),
        _ => throw new UnmappedException($"primitive {p.Kind}"),
    };
}

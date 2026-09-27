using HexaGen.CppAst.Model.Declarations;
using HexaGen.CppAst.Model.Types;

namespace RmlShimGen;

// How the generator sees a C++ type: one level of indirection (Indirection) over a base type (a Base record). Every
// rule in TypeMapper is a switch on that pair; a pair with no case throws UnmappedException, and the function lands in
// the report with the message as its reason. Adding a kind of type is adding a Base record and its cases, here and in
// CsLayer; docs/walkthroughs.md walks through one.

/// <summary>The one level of reference or pointer over a type's base; deeper indirection is not mapped.</summary>
public enum Indirection
{
    /// <summary>By value.</summary>
    None,

    /// <summary>A reference, T&amp;.</summary>
    Ref,

    /// <summary>A pointer, T*.</summary>
    Ptr,
}

/// <summary>A C++ type with its qualifiers and one indirection removed, classified by how it can cross into C.</summary>
public abstract record Base;

/// <summary>void: a void return, or void* by pointer.</summary>
public sealed record VoidBase : Base;

/// <summary>Rml::String (std::string): UTF-8 <c>const char*</c> in C, <c>string</c> in C#.</summary>
public sealed record StringBase : Base;

/// <summary>A primitive or fixed-size C typedef, by its C spelling.</summary>
/// <param name="CName">The C type (<c>int32_t</c>, <c>float</c>, <c>size_t</c>).</param>
/// <param name="Widen">
/// For C++ <c>long</c>/<c>unsigned long</c>, which differ in size between Windows and Linux: the C++ type to convert to
/// from the 64-bit C type. Null when C and C++ agree.
/// </param>
public sealed record PrimBase(string CName, string? Widen) : Base;

/// <summary>A class in shim.json's Classes or OpaqueTypes: an opaque pointer in C, a handle struct in C#.</summary>
public sealed record HandleBase(CppClass Class) : Base;

/// <summary>A ValueTypes typedef (Rml::Vector2f): a C struct of the same fields, copied with std::bit_cast.</summary>
/// <param name="CppName">The C++ typedef (<c>Rml::Vector2f</c>).</param>
/// <param name="CName">The C struct (<c>RmlVector2f</c>).</param>
public sealed record ValueBase(string CppName, string CName) : Base;

/// <summary>A named enum: a C enum whose every value is static_asserted against the C++ one.</summary>
public sealed record EnumBase(CppEnum Enum) : Base;

/// <summary>Rml::Span of a const primitive: a pointer and a count.</summary>
/// <param name="ElementCName">The C type of the items.</param>
public sealed record SpanBase(string ElementCName) : Base;

/// <summary>A std::vector of String or of a handle pointer (StringList, ElementList).</summary>
public sealed record ListBase(Base Element) : Base;
/// <summary>An owning RmlUi pointer, std::unique_ptr&lt;T, Rml::Releaser&lt;T&gt;&gt; (ElementPtr).</summary>
public sealed record OwnedBase(HandleBase Handle) : Base;
/// <summary>Rml::Variant, copied across as its exact stored value (RmlVariant).</summary>
public sealed record VariantBase : Base;
/// <summary>A map of String to Variant (Dictionary, ElementAttributes), copied across as RmlDictionary.</summary>
public sealed record DictionaryBase : Base;

/// <summary>How one C++ parameter crosses: its C declarations, and the C++ that rebuilds the argument.</summary>
/// <param name="CDecls">The C parameter declarations it becomes; usually one, two for a pointer and count.</param>
/// <param name="CppArg">The C++ expression passed to RmlUi, built from the C parameters.</param>
/// <param name="Pre">C++ statements run before the call (building a temporary the call writes into).</param>
/// <param name="Post">C++ statements run after the call (copying an output back to the C side).</param>
public sealed record ParamMap(List<string> CDecls, string CppArg, List<string> Pre, List<string> Post)
{
    /// <summary>The classified base type; CsLayer maps the C# side from it.</summary>
    public Base Base { get; init; } = new VoidBase();

    /// <summary>The reference or pointer over <see cref="Base"/>.</summary>
    public Indirection Ind { get; init; }

    /// <summary>Whether what the reference or pointer points at is const.</summary>
    public bool PointeeConst { get; init; }

    /// <summary>The C and C++ parameter name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Written by the callee, never read: documented <c>@param[out]</c> in RmlUi's header.</summary>
    public bool IsOut { get; init; }
}

/// <summary>How a C++ return value crosses: its C type, and the statements that return it given the value in <c>rml_ret</c>.</summary>
/// <param name="CType">The C return type.</param>
/// <param name="IsVoid">Whether the function returns nothing (no <c>rml_ret</c> is declared).</param>
/// <param name="Statements">C++ statements that turn <c>rml_ret</c> into the C return.</param>
public sealed record ReturnMap(string CType, bool IsVoid, List<string> Statements)
{
    /// <summary>The classified base type; CsLayer maps the C# side from it.</summary>
    public Base Base { get; init; } = new VoidBase();

    /// <summary>The reference or pointer over <see cref="Base"/>.</summary>
    public Indirection Ind { get; init; }

    /// <summary>Whether what the reference or pointer points at is const.</summary>
    public bool PointeeConst { get; init; }
}

/// <summary>Thrown (and caught per function) when a type has no rule; the message goes into the report.</summary>
/// <param name="reason">The report heading the function is listed under: the type, or what about it has no rule.</param>
public sealed class UnmappedException(string reason) : Exception(reason);

/// <summary>
/// The type rules: how each C++ parameter and return type crosses into C (<see cref="MapParam(CppType, string, bool)"/>,
/// <see cref="MapReturn(CppType)"/>), and which shared C types (lists, enums, Variant) the mapped signatures reach, so the
/// header can declare them.
/// </summary>
public sealed class TypeMapper
{
    // Typedefs that C spells the same way; kept by name so size_t stays size_t on both sides.
    private static readonly HashSet<string> CTypedefs =
    [
        "size_t", "ptrdiff_t", "intptr_t", "uintptr_t",
        "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    ];

    private readonly HashSet<string> handleClasses;
    private readonly HashSet<string> valueTypes;

    /// <summary>A mapper for the handle and value types the config names.</summary>
    public TypeMapper(ShimConfig config)
    {
        handleClasses = [.. config.Classes, .. config.OpaqueTypes];
        valueTypes = [.. config.ValueTypes];
    }

    /// <summary>List structs reached by any mapped signature: C name to the C type of its items.</summary>
    public SortedDictionary<string, string> UsedLists { get; } = new(StringComparer.Ordinal);

    /// <summary>Rml::Variant::Type, once any mapped signature reaches a Variant or Dictionary; the shim's helpers need it.</summary>
    public CppEnum? VariantType { get; private set; }

    /// <summary>Classes whose owning pointer (OwnedBase) any mapped signature reaches; each gets a release function.</summary>
    public SortedSet<string> UsedOwned { get; } = new(StringComparer.Ordinal);

    /// <summary>C name of the function that releases an owned object nothing else took.</summary>
    public static string ReleaseOwnedCName(string qualified) => HandleCName(qualified) + "_ReleaseOwned";

    /// <summary>Enums reached by any mapped signature, keyed by qualified name.</summary>
    public SortedDictionary<string, CppEnum> UsedEnums { get; } = new(StringComparer.Ordinal);

    /// <summary>The C name of a handle: <c>Rml::ElementDocument</c> becomes <c>RmlElementDocument</c>.</summary>
    public static string HandleCName(string qualified) => "Rml" + StripRml(qualified).Replace("::", "");

    /// <summary>The C name of an enum: <c>Rml::Style::Display</c> becomes <c>RmlStyleDisplay</c>.</summary>
    public static string EnumCName(CppEnum e) => "Rml" + StripRml(e.FullName).Replace("::", "");

    /// <summary>The C name of a value type: <c>Rml::Vector2f</c> becomes <c>RmlVector2f</c>.</summary>
    public static string ValueCName(string qualified) => "Rml" + StripRml(qualified).Replace("::", "");

    private static string StripRml(string qualified) => qualified.StartsWith("Rml::") ? qualified[5..] : qualified;

    /// <summary>How a parameter crosses into C.</summary>
    /// <param name="type">The parameter's C++ type, as parsed (template parameters already substituted).</param>
    /// <param name="name">The parameter's name in C and in the C++ the shim writes.</param>
    /// <param name="documentedOut">
    /// Whether RmlUi's doc comment marks it <c>@param[out]</c>. Only a non-const reference or pointer can be an output;
    /// a String or list reference that is not documented as one has no rule, since it might be read.
    /// </param>
    /// <exception cref="UnmappedException">No rule covers the type.</exception>
    public ParamMap MapParam(CppType type, string name, bool documentedOut)
    {
        var (b, ind, pointeeConst) = Classify(type);
        bool isOut = documentedOut && ind != Indirection.None && !pointeeConst;
        return MapParam(b, ind, pointeeConst, isOut, name) with { Base = b, Ind = ind, PointeeConst = pointeeConst, Name = name, IsOut = isOut };
    }

    private ParamMap MapParam(Base b, Indirection ind, bool pointeeConst, bool isOut, string name)
    {
        string c = pointeeConst ? "const " : "";
        switch (b)
        {
            case StringBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"const char* {name}"], $"Rml::String({name} ? {name} : \"\")", [], []);

            case StringBase when isOut:
                // Parked per thread like a String return: valid until this function is next called on the thread.
                return new(
                    [$"const char** {name}"],
                    ind == Indirection.Ref ? $"{name}_tmp" : $"&{name}_tmp",
                    [$"Rml::String {name}_tmp;"],
                    [
                        $"thread_local Rml::String {name}_out;",
                        $"{name}_out = std::move({name}_tmp);",
                        $"if ({name}) *{name} = {name}_out.c_str();",
                    ]);

            case StringBase when ind != Indirection.None:
                throw new UnmappedException($"String {ind} parameter not documented @param[out]");

            case ListBase l when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"{UseList(l)} {name}"], $"{name}_tmp", ListFromC(l, name), []);

            case VariantBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"RmlVariant {name}"], $"rmlc_variant_in({name})", [], []);

            case DictionaryBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new([$"RmlDictionary {name}"], $"{name}_tmp", [$"Rml::Dictionary {name}_tmp = rmlc_dictionary_in({name});"], []);

            case OwnedBase o when ind == Indirection.None:
                // Ownership passes to RmlUi with the call.
                UsedOwned.Add(o.Handle.Class.FullName);
                return new([$"{HandleCName(o.Handle.Class.FullName)}* {name}"],
                    $"Rml::UniqueReleaserPtr<{o.Handle.Class.FullName}>(reinterpret_cast<{o.Handle.Class.FullName}*>({name}))", [], []);

            case ListBase l when isOut && ind == Indirection.Ref:
                return new(
                    [$"{UseList(l)}* {name}"],
                    $"{name}_tmp",
                    [$"{ListCppType(l)} {name}_tmp;"],
                    [.. ListToC(l, $"{name}_tmp", $"{name}_out", out var expr), $"if ({name}) *{name} = {expr};"]);

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

    /// <summary>How a return value crosses into C.</summary>
    /// <exception cref="UnmappedException">No rule covers the type.</exception>
    public ReturnMap MapReturn(CppType type)
    {
        var (b, ind, pointeeConst) = Classify(type);
        return MapReturn(b, ind, pointeeConst) with { Base = b, Ind = ind, PointeeConst = pointeeConst };
    }

    private ReturnMap MapReturn(Base b, Indirection ind, bool pointeeConst)
    {
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

            case ListBase l when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new(UseList(l), false, [.. ListToC(l, "rml_ret", "rml_list", out var expr), $"return {expr};"]);

            case VariantBase when ind == Indirection.Ptr:
                // Points into its owner (an element's attributes); the C# side copies it before anything can change it.
                return new("RmlVariant", false, ["return rmlc_variant_out(rml_ret);"]);

            case VariantBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new("RmlVariant", false, ["thread_local Rml::Variant rml_variant;", "rml_variant = rml_ret;", "return rmlc_variant_out(&rml_variant);"]);

            case DictionaryBase when ind == Indirection.None || (ind == Indirection.Ref && pointeeConst):
                return new("RmlDictionary", false,
                [
                    "thread_local Rml::Dictionary rml_dictionary;",
                    "thread_local Rml::Vector<RmlDictionaryEntry> rml_entries;",
                    "rml_dictionary = rml_ret;",
                    "return rmlc_dictionary_out(rml_dictionary, rml_entries);",
                ]);

            case OwnedBase o when ind == Indirection.None:
                // Ownership passes to the caller, who releases it with Rml{Class}_ReleaseOwned or hands it back.
                UsedOwned.Add(o.Handle.Class.FullName);
                return new($"{HandleCName(o.Handle.Class.FullName)}*", false,
                    [$"return reinterpret_cast<{HandleCName(o.Handle.Class.FullName)}*>(rml_ret.release());"]);

            default:
                throw new UnmappedException($"{Describe(b)} {ind}{(pointeeConst ? " const" : "")} return");
        }
    }

    /// <summary>A base type as the report names it.</summary>
    public static string Describe(Base b) => b switch
    {
        VoidBase => "void",
        StringBase => "String",
        PrimBase p => p.CName,
        HandleBase h => h.Class.FullName,
        ValueBase v => v.CppName,
        EnumBase e => e.Enum.FullName,
        SpanBase s => $"Span<{s.ElementCName}>",
        ListBase l => $"List<{Describe(l.Element)}>",
        OwnedBase o => $"Owned<{o.Handle.Class.FullName}>",
        VariantBase => "Rml::Variant",
        DictionaryBase => "Dictionary",
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
        _ => type.ToString() ?? type.GetType().Name,
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
                case CppClass cls when cls.FullName == "Rml::Variant":
                    UseVariant(cls);
                    return new VariantBase();
                case CppClass cls when AsDictionary(cls) is { } variant:
                    UseVariant(variant);
                    return new DictionaryBase();
                case CppClass cls when cls.FullName.StartsWith("Rml::Span<", StringComparison.Ordinal):
                    return MapSpan(cls);
                // Template arguments arrive with RmlUi's String typedef already looked through.
                case CppClass cls when cls.Name == "basic_string"
                    && cls.TemplateSpecializedArguments.FirstOrDefault()?.ArgAsType is CppPrimitiveType { Kind: CppPrimitiveKind.Char }:
                    return new StringBase();
                case CppClass cls when cls.Name == "vector" && cls.TemplateSpecializedArguments.Count > 0:
                    return MapList(cls, viaTypedef);
                case CppClass cls when cls.Name == "unique_ptr" && cls.TemplateSpecializedArguments.Count == 2:
                    return MapOwned(cls, viaTypedef);
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

    private ListBase MapList(CppClass vector, string? viaTypedef)
    {
        string what = viaTypedef ?? vector.FullName;
        var arg = vector.TemplateSpecializedArguments[0].ArgAsType ?? throw new UnmappedException($"{what} (no template argument exposed)");
        arg = ResolveTransparentTypedefs(StripQualifiers(arg, out _));
        Base element;
        try
        {
            element = arg is CppPointerType p ? ClassifyBase(p.ElementType) : ClassifyBase(arg);
        }
        catch (UnmappedException)
        {
            throw new UnmappedException(what);
        }
        return (element, arg is CppPointerType) switch
        {
            (HandleBase, true) or (StringBase, false) => new ListBase(element),
            _ => throw new UnmappedException(what),
        };
    }

    /// <summary>
    /// A map from String to Variant, whichever container RmlUi's config picked for SmallUnorderedMap (itlib::flat_map or
    /// an unordered map): recognised by its template arguments, not its name. Returns the Variant class.
    /// </summary>
    private CppClass? AsDictionary(CppClass cls)
    {
        var args = cls.TemplateSpecializedArguments;
        if (args.Count < 2 || args[1].ArgAsType is not CppClass { FullName: "Rml::Variant" } variant || args[0].ArgAsType is not { } key)
        {
            return null;
        }
        try
        {
            return ClassifyBase(key) is StringBase ? variant : null;
        }
        catch (UnmappedException)
        {
            return null;
        }
    }

    private void UseVariant(CppClass variant)
    {
        VariantType ??= variant.Enums.FirstOrDefault(e => e.Name == "Type") ?? throw new UnmappedException("Rml::Variant has no Type enum");
        UsedEnums[VariantType.FullName] = VariantType;
    }

    /// <summary>Only RmlUi's Releaser deleter: C# can hand the object back to RmlUi, but cannot run another deleter.</summary>
    private OwnedBase MapOwned(CppClass uniquePtr, string? viaTypedef)
    {
        string what = viaTypedef ?? uniquePtr.FullName;
        var args = uniquePtr.TemplateSpecializedArguments;
        if (args[1].ArgAsType is not CppClass { Name: "Releaser" } || args[0].ArgAsType is not { } pointee)
        {
            throw new UnmappedException(what);
        }
        try
        {
            return ClassifyBase(pointee) is HandleBase h ? new OwnedBase(h) : throw new UnmappedException(what);
        }
        catch (UnmappedException)
        {
            throw new UnmappedException(what);
        }
    }

    /// <summary>The C struct for a list: <c>RmlStringArray</c>, or <c>Rml{Class}Array</c> for handles.</summary>
    public static string ListCName(ListBase l) => l.Element switch
    {
        StringBase => "RmlStringArray",
        HandleBase h => HandleCName(h.Class.FullName) + "Array",
        _ => throw new InvalidOperationException(Describe(l)),
    };

    /// <summary>Records that a signature uses this list struct, so the header declares it; returns its C name.</summary>
    private string UseList(ListBase l)
    {
        string cName = ListCName(l);
        UsedLists[cName] = l.Element is HandleBase h ? $"{HandleCName(h.Class.FullName)}*" : "const char*";
        return cName;
    }

    /// <summary>The C++ vector the shim builds or copies for a list.</summary>
    private static string ListCppType(ListBase l) => l.Element is HandleBase h ? $"Rml::Vector<{h.Class.FullName}*>" : "Rml::Vector<Rml::String>";

    /// <summary>Statements that build the C++ list <c>{name}_tmp</c> from the C list <c>name</c>.</summary>
    private static List<string> ListFromC(ListBase l, string name) => l.Element switch
    {
        HandleBase h =>
        [
            $"auto {name}_data = reinterpret_cast<{h.Class.FullName}* const*>({name}.data);",
            $"{ListCppType(l)} {name}_tmp({name}_data, {name}_data + {name}.count);",
        ],
        _ =>
        [
            $"{ListCppType(l)} {name}_tmp;",
            $"{name}_tmp.reserve({name}.count);",
            $"for (size_t rml_i = 0; rml_i < {name}.count; rml_i++) {name}_tmp.emplace_back({name}.data[rml_i] ? {name}.data[rml_i] : \"\");",
        ],
    };

    /// <summary>
    /// Statements that park a C++ list per thread (valid until this function is next called on the thread), and the
    /// C struct expression that points at it.
    /// </summary>
    private static List<string> ListToC(ListBase l, string source, string storage, out string expr)
    {
        var lines = new List<string> { $"thread_local {ListCppType(l)} {storage};", $"{storage} = std::move({source});" };
        if (l.Element is HandleBase h)
        {
            expr = $"{ListCName(l)}{{ reinterpret_cast<void* const*>({storage}.data()), {storage}.size() }}";
        }
        else
        {
            lines.Add($"thread_local Rml::Vector<const char*> {storage}_ptrs;");
            lines.Add($"{storage}_ptrs.clear();");
            lines.Add($"for (const auto& rml_item : {storage}) {storage}_ptrs.push_back(rml_item.c_str());");
            expr = $"{ListCName(l)}{{ {storage}_ptrs.data(), {storage}_ptrs.size() }}";
        }
        return lines;
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

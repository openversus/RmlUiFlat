# The generator

`gen/RmlShimGen` turns RmlUi's C++ headers into a C API and a C# API over it. This page explains how, in the order
it happens, then gives the rules it applies and a reference for its config. To change it, read
[walkthroughs.md](walkthroughs.md); to check a change, [testing.md](testing.md).

```sh
dotnet run --project gen/RmlShimGen            # reads gen/shim.json; about 90 s on the machine this was written on
```

## What runs, in order

**1. Parse** (`Program.cs`). RmlUi's headers are parsed as one translation unit, `EntryHeader` (`RmlUi/Core.h`),
with HexaGen.CppAst (libclang underneath): C++17, macros and comments kept, targeting x86-64 Linux. The target only
has to get the parse through libstdc++'s headers; the C ABI does not depend on it, because the generated C++ checks
every layout with `static_assert` on the compiler that builds the shim, for each real target. A parse error stops
the run.

**2. Index** (`ShimGenerator.Run`, `Index`). Every class and typedef in the parse, by qualified name. Each class in
shim.json's `Classes` and `OpaqueTypes` must be found, or the run stops.

**3. Value types** (`BuildValueStructs`). For each `ValueTypes` typedef (`Rml::Vector2f`), the C struct is derived
from the class template's primitive fields (`Vector2<float>` gives `float x, y`). A value type that cannot be derived
stops the run.

**4. Wrap** (`WrapGroup`). Every free function in `FreeFunctionHeaders` and every public method of each `Classes`
entry is considered, in groups of one name's overloads:

- Constructors, destructors, operators, deleted functions and templates are skipped, unless the template is listed
  in `TemplateInstantiations`, in which case its template parameter is replaced by the listed type.
- RmlUi's RTTI methods (`IsClass`, `GetClassIdentifier`, `GetStaticClassIdentifier`) are skipped on classes that
  declare them; the generated `From` casts use them instead.
- The C name is chosen (below), then `Renames` and `IgnoredFunctions` apply. Variadic functions, and the const
  overload of a const/non-const pair (the same C name twice), are reported and skipped.
- Which parameters are outputs is read from RmlUi's doc comment (`@param[out] name`), from CppAst's parsed comment
  tree. An override with no comment of its own takes the comment of the method it overrides.
- The return and each parameter go through `TypeMapper` (below). If any type has no rule, the function is reported
  under that reason and skipped; nothing else about it is generated.

**5. Write C** (`WriteHeader`, `WriteSource`). `native/generated/rmlui_c.h`: handles as opaque structs, value types,
list structs, enums, `RmlVariant` and `RmlDictionary`, upcasts, release functions for owned objects, checked
downcasts, then every wrapped function with RmlUi's doc comment and the C++ signature. `rmlui_c.cpp`: the
`static_assert`s (every value type's size, alignment and field offsets; every enum item's value), the Variant helpers,
the casts, and one body per function, which rebuilds the C++ arguments, calls RmlUi, and turns the result into C.

**6. Write C#** (`CsLayer.cs`, `WriteCsLayer`). `src/RmlUi.Net/Api.Generated/`, recreated from scratch each run:

- `NativeMethods.cs`: a `LibraryImport` for every C function, and the interop structs.
- `Rml.cs`: the free functions, as `Rml.*`.
- One file per handle class: a `readonly struct` over the pointer, with implicit upcasts, `From` downcasts, its
  methods, and its base classes' methods forwarded (C# does not look through implicit conversions for members).
- `Enums.cs` and `ValueTypes.cs`.

It declares its own imports rather than calling HexaGen's raw layer, so HexaGen's naming never has to be predicted.

**7. Report** (`WriteReport`). `gen/shim-report.md`: counts; every in-scope function that could not be mapped,
grouped by reason; C# names that differ from C++; and every RmlUi class with public methods that is not in scope.
After an RmlUi update, its diff is the list of what changed shape.

**8. Raw layer** (`Program.cs`). HexaGen's C-to-C# generator runs over `rmlui_c.h` and `rmlui_c_manual.h` into
`src/RmlUi.Net/Generated/` (namespace `RmlUi.Interop`), configured by `gen/hexagen.json`. It is there for anything the
C# API does not cover yet; nothing in this repository uses it.

## Names

- **C handles and types**: `Rml` + the qualified name without `Rml::` and `::` (`RmlElementDocument`,
  `RmlStyleDisplay`, `RmlVector2f`).
- **C functions**: `Rml{Class}_{Method}`, or `Rml{Function}` for a free function. When a name has more than one
  overload, **every** overload gets a suffix from its parameter types (`RmlGetContext_String`, `RmlGetContext_Int`),
  counting overloads the generator cannot map. So a new rule that maps another overload never renames an existing
  C function. (An instantiated template joins its name's overloads; see `TemplateInstantiations` below.)
- **Casts**: `Rml{Derived}_As{Base}` upcasts; `Rml{Derived}_From{Base}` checked downcasts, generated where both
  classes declare RmlUi's RTTI (`rmlui_dynamic_cast`, which returns null for another type).
- **C#**: the C++ name, with C# overloading. If two overloads would have the same C# signature, or a method would
  clash with the type's own name or one of the handle's members, the later one takes its C name's suffix as its C#
  name; the report lists every such case. Parameter names become camelCase; one that is a C# keyword gets `@`.
- **Special C# shapes**:
  - A class's `String ToString() const` becomes its C# `ToString()` override (null-safe).
  - A `void` function whose only output is a string or a list returns it (`QuerySelectorAll(selector)` returns
    `Element[]`).
  - RmlUi's namespaces become static classes (`Style.FontWeight.Bold`); an enum declared in a class nests in its
    handle struct (`ElementScroll.Orientation`).

## How each type crosses

One level of reference or pointer over a base type; anything deeper is not mapped. Each row is a case in
`TypeMapper.MapParam`/`MapReturn` (C side) and `CsLayer.MapCsParam`/`MapCsReturn` (C# side).

| C++ | C | C# | Notes |
|---|---|---|---|
| `const String&`, `String` parameter | `const char*` (UTF-8) | `string` | null reads as empty |
| `const String&` return | `const char*` | `string` (copied) | owned by RmlUi, valid while its owner is |
| `String` return | `const char*` | `string` (copied) | a per-thread copy in C, valid until the same function is next called on the thread |
| class in `Classes`/`OpaqueTypes` | opaque pointer | handle struct | upcasts implicit in C#; checked `From` downcasts |
| `ValueTypes` (Vector2f, Colourb, ...) | C struct, copied by `std::bit_cast` | struct of the same fields | size, alignment and offsets `static_assert`ed |
| enum | C enum | `enum : int` | every value `static_assert`ed |
| `Span<const T>` (T primitive) | `const T*, size_t` | `T*, nuint` | a pointer, not a span: RmlUi may keep it (LoadFontFace does, until Shutdown) |
| `StringList`, `ElementList` return (a `std::vector` of `String` or a handle pointer) | `RmlStringArray`, `Rml{Name}Array` | `string[]`, `Element[]` (copied) | a per-thread copy in C, valid until the same function is next called on the thread; handle items are `void*` in C (HexaGen cannot wrap a handle-pointer-pointer field) |
| list parameter | the same struct, by value | `IEnumerable<string>`, `IEnumerable<Element>` | copied into a C++ list for the call |
| `@param[out]` `String&`, `String*` | `const char**` | `out string` | a per-thread copy in C |
| `@param[out]` list | `Rml{Name}Array*` | `out T[]` | |
| other `@param[out]` references and pointers | `T*` | `out T` | a non-const reference not documented `[out]` stays `ref`; a non-const String or list reference not documented `[out]` is not mapped, since RmlUi might read it |
| `ElementPtr` (`unique_ptr<T, Releaser<T>>`) return | `RmlElement*`, released to the caller | `OwnedElement?` | `RmlElement_ReleaseOwned` releases one never handed back |
| `ElementPtr` parameter | `RmlElement*`, taken by RmlUi | `OwnedElement?` | taken on the call, whatever it returns; null throws `ArgumentNullException` |
| `Variant` | `RmlVariant`: type and exact stored value | `Variant` (`Variant?` for `Variant*`) | read with `GetReference`, no RmlUi `TypeConverter` (its table has gaps, which assert in debug); C# converts; type 0 is a null `Variant*` |
| `Dictionary`, `ElementAttributes` | `RmlDictionary`: key and `RmlVariant` entries | `IReadOnlyDictionary<string, Variant>` out, `IEnumerable<KeyValuePair<string, Variant>>` in | recognised by its template arguments (String, Variant), whichever container RmlUi's config picks |
| function template listed in `TemplateInstantiations` | one C function for that argument | a method | `SetAttribute<Rml::Variant>`, `Event::GetParameter<Rml::String>` |
| `bool` | `bool` | `bool` | one byte on the wire |
| `long` | `int64_t` | `long` | 32-bit on Windows, 64-bit on Linux; the shim converts |

Everything else is reported as unmapped, under the type that stopped it.

## shim.json

Every key, with what it does. The XML docs on `ShimConfig.cs` say the same in more detail.

| Key | What it does |
|---|---|
| `RmlUiInclude` | RmlUi's public include directory. |
| `EntryHeader` | The header parsed as the translation unit (`RmlUi/Core.h`). |
| `SystemIncludeFolders` | The C++ standard library and compiler include paths **on this machine**; adjust for another. |
| `Defines` | Preprocessor definitions for the parse (`RMLUI_STATIC_LIB`). |
| `FreeFunctionHeaders` | Headers whose free functions in namespace `Rml` are wrapped (`RmlUi/Core/Core.h`). |
| `Classes` | Classes whose public methods are wrapped. |
| `OpaqueTypes` | Classes that cross only as handles, no methods. |
| `ValueTypes` | Typedefs passed by value as C structs. |
| `IgnoredFunctions` | Functions to leave out: a qualified C++ name (all its overloads) or a C name (one overload). |
| `TemplateInstantiations` | Function templates to wrap: qualified name → the type for its one template parameter. Instantiating a template that shares its name with other overloads gives them all suffixes, so their C names change. |
| `Renames` | C name → final C name. |
| `NativeOutput`, `CSharpOutput`, `CsApiOutput`, `ReportOutput`, `HexaGenConfig` | Where things are written and read. |
| `LibraryName` | The library name in the C# imports (`rmlui_c`). |

## Things the generator relies on

- **The generated C++ is the check.** Layouts and enum values are `static_assert`ed, so a wrong assumption about a
  type fails the shim's compile, on each target, instead of corrupting memory at run time. This caught a bug in the
  generator itself on its first compile.
- **Per-thread copies.** Strings, lists and dictionaries returned by value live in `thread_local` storage inside each
  C function until C# copies them, straight after the call.
- **Determinism.** Regenerating with nothing changed must produce byte-identical output; [testing.md](testing.md)
  uses that.

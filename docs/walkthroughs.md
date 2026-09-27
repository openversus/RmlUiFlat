# Walkthroughs

Four changes you will make to the bindings, step by step. The first two were run exactly as written, and the output
quoted is what they produced (the second is how `Event::GetParameter` came to be wrapped). The third retraces how an existing rule (lists) was added. The fourth is the checklist
for moving to a new RmlUi.

After any of them, run the sequence in [testing.md](testing.md). For how the generator works, see
[generator.md](generator.md).

- [Bring an RmlUi class into scope](#bring-an-rmlui-class-into-scope)
- [Wrap a function template](#wrap-a-function-template)
- [Add a type rule](#add-a-type-rule)
- [Update RmlUi](#update-rmlui)

## Bring an RmlUi class into scope

**When:** you need methods of an RmlUi class the C# API does not have. The example is `Rml::ElementUtilities`,
RmlUi's static helpers for finding and laying out elements.

**1. Find it.** The end of `gen/shim-report.md` lists every RmlUi class with public methods that is not in scope:

```
- `Rml::ElementUtilities` (13 methods)
```

**2. Add it to `Classes`** in `gen/shim.json`:

```json
  "Classes": [
    ...
    "Rml::Property",
    "Rml::ElementUtilities"
  ],
```

**3. Regenerate** (`dotnet run --project gen/RmlShimGen`) and read the report's diff (`git diff gen/shim-report.md`):

```
< Wrapped 304 functions; 40 in scope could not be mapped; 1 ignored by shim.json.
> Wrapped 314 functions; 43 in scope could not be mapped; 1 ignored by shim.json.

> ### Rml::Rectanglef (1)
> - `bool Rml::ElementUtilities::GetBoundingBox(Rectanglef& out_rectangle, Element* element, BoxArea area)`
> ### Rml::Rectanglei (1)
> - `bool Rml::ElementUtilities::GetClippingRegion(Element* element, Rectanglei& clip_region, ClipMaskGeometryList* clip_mask_list = nullptr, bool force_clip_self = false)`
> ### Rml::StringView (1)
> - `int Rml::ElementUtilities::GetStringWidth(Element* element, StringView string, Character prior_character = Character::Null)`

< - `Rml::ElementUtilities` (13 methods)
```

Ten of its thirteen methods are wrapped. Three are not, each under the type that has no rule. Decide whether you need
them: if you do, that type needs a rule ([Add a type rule](#add-a-type-rule)), or, for a plain struct, possibly an
entry in `ValueTypes`.

**4. Read what was generated.** `src/RmlUi.Net/Api.Generated/ElementUtilities.cs` has the methods as C# statics:

```csharp
public static Element GetElementById(Element rootElement, string id)
public static Element[] GetElementsByTagName(Element rootElement, string tag, string stopTag = "")
public static float GetDensityIndependentPixelRatio(Element element)
public static bool PositionElement(Element element, Vector2f offset, ElementUtilities.PositionAnchor anchor)
...
```

Its nested enum came with it (`ElementUtilities.PositionAnchor`). The C API gained `RmlElementUtilities_*` functions.

**5. Build and test.** The [testing.md](testing.md) sequence, including the Windows export check: the DLL gains the
new functions. If you use one of them for something that matters, add a self-test check with a known value.

## Wrap a function template

**When:** RmlUi offers something only as a template. The generator skips templates, because it cannot know which
type arguments you want. The example is `Event::GetParameter<T>(key, default_value)`, wrapped this way to give C#
a direct way to read one event parameter as a string. The output below is from the run that added it, when the
report stood at 303.

**1. Add it to `TemplateInstantiations`** in `gen/shim.json`, with the type for its one template parameter:

```json
  "TemplateInstantiations": { "Rml::Element::SetAttribute": "Rml::Variant", "Rml::Event::GetParameter": "Rml::String" },
```

A comment above the entry says what it is for; `shim.json` allows `//` comments.

One instantiation per template: the key is the template's qualified name. The template must have exactly one type
parameter.

**2. Regenerate and read the diffs.** The report gains one function and nothing else changes:

```
< Wrapped 303 functions; 40 in scope could not be mapped; 1 ignored by shim.json.
> Wrapped 304 functions; 40 in scope could not be mapped; 1 ignored by shim.json.
```

`rmlui_c.h` gains:

```c
RMLC_API const char* RmlEvent_GetParameter(RmlEvent* self, const char* key, const char* default_value);
```

whose body calls the instantiation explicitly: `->GetParameter<Rml::String>(...)`. And the C# `Event` gains:

```csharp
public string GetParameter(string key, string defaultValue)
```

The generated doc comment still shows the C++ signature with `T`; that is the header's spelling.

**3. Check for renamed functions.** A template joins its name's overloads. If it shares a name with a function that
had none, that function's C name gains an overload suffix, and anything importing the old C name breaks. Look for a
changed `RMLC_API` line in `git diff native/generated/rmlui_c.h`, not only added ones. (`GetParameter` has no other
overloads, so nothing was renamed here.)

**4. Build and test**: the [testing.md](testing.md) sequence. For `GetParameter`, the self-test reads a custom
event's `"s"` parameter (`"text"`) and a missing key (which must return the default), and a mutation that makes the
shim look up the wrong key fails it.

## Add a type rule

**When:** the report lists functions you need under a type that has no rule. This retraces how lists (`StringList`,
`ElementList`) were added: the report had `### Rml::ElementList (3)` and `### Rml::StringList (3)`, with
`QuerySelectorAll`, `GetActivePseudoClasses` and `ArePseudoClassesSet` among them.

**1. Decide the shapes before writing code**, in C and in C#, and write them down. The C# shape is the one that
matters to the people using it: a list should be an ordinary array coming back and any `IEnumerable` going in, not a
pointer and a count. For lists:

| | C | C# |
|---|---|---|
| returned | `RmlStringArray { const char* const* data; size_t count; }`, pointing into a per-thread copy | `string[]`, copied at once |
| parameter | the same struct, by value | `IEnumerable<string>` |
| `@param[out]` | a pointer to the struct | returned, when it is a void function's only output |

Then read RmlUi's source for anything the shape depends on: which parameters RmlUi writes (its `@param[out]`
comments), whether it keeps a pointer you pass it, and whether a conversion can assert. Variant's rule copies the
stored value exactly for that last reason.

**2. Recognise the type** (`gen/RmlShimGen/TypeMapper.cs`). Add a `Base` record (`ListBase(Base Element)`) and a case in
`ClassifyBase` that returns it. Recognise the C++ type by its structure, not its spelling: lists are `std::vector`
specializations whose element classifies as a String or a handle pointer (`MapList`), whatever typedef names them.
Add it to `Describe` for the report.

**3. Map it to C** (`TypeMapper.MapParam`, `MapReturn`). Each case returns the C declarations, the C++ expression
that rebuilds the argument, and the statements before and after the call (`ParamMap`), or the statements that turn
`rml_ret` into the C return (`ReturnMap`). Guard each case with the indirection and constness it handles; anything
else falls through to "no rule". If the C side needs shared declarations (the list structs), record them (`UseList`,
`UsedLists`) and emit them in `ShimGenerator.WriteHeader`; shared C++ helpers go in `WriteSource`.

**4. Map it to C#** (`CsLayer.MapCsParam`, `MapCsReturn`). Each parameter case gives the C# declaration, the
`LibraryImport` declaration, and the code before the call, around it (`fixed`), after it and in `finally`. Interop
structs and copy helpers go in `WriteNativeMethods`.

**5. Regenerate and read the result.** The report's section for that type disappears (or shrinks to the cases you
chose not to cover). Read one generated function in each direction in `rmlui_c.cpp` and in the C# file, as a reviewer
would.

**6. Build, then prove it with known values.** Add self-test checks whose expected values are known, not merely
non-empty: `QuerySelectorAll("input, button")` must return exactly the input and the button, in document order;
`ArePseudoClassesSet(["hover", "no-such-class"])` must be false. Then break the rule in the generated C++ (pass only
the first item of the list) and watch the check fail ([testing.md](testing.md#can-the-test-fail)).

**7. Document it**: the row in [generator.md](generator.md#how-each-type-crosses), and [using.md](using.md) if it
changes what someone writes.

## Update RmlUi

`external/RmlUi` is a submodule pinned to 3045e6e (6.4-dev). Moving it:

**1. Move the submodule** to the new commit and commit nothing yet.

**2. Regenerate and read the report's diff.** It is the list of what changed shape: functions that stopped mapping
(a new type in a signature), functions that appeared, classes that appeared or went, C# names that changed. Decide
each one.

**3. Build the shim on both targets.** The generated `static_assert`s check every value type's layout and every enum
value against the new headers. One that fails after regenerating means the generator's assumption about that type is
out of date; that is the check doing its job.

**4. Recheck what mirrors RmlUi by hand:**

- `ProbeVulkan` in `native/backend/rmlui_c_backend.cpp` against `Initialize_Device` and the other `Initialize_*`
  functions in `RmlUi_Renderer_VK.cpp`: the preflight must ask for what RmlUi will ask for.
- Type names shared between backend files linked into one binary. The OpenGL renderer is isolated in its own
  namespace; the Vulkan renderer and the SDL platform are not. This must print nothing:

  ```sh
  B=external/RmlUi/Backends
  names() { cat "$@" | grep -ohE '^\s*(struct|class|enum class|enum|namespace) [A-Za-z_][A-Za-z0-9_]*' | awk '{print $NF}' | sort -u; }
  comm -12 <(names $B/RmlUi_Renderer_VK.cpp $B/RmlUi_Renderer_VK.h) <(names $B/RmlUi_Platform_SDL.cpp $B/RmlUi_Platform_SDL.h)
  ```

  (With `RmlUi_Renderer_GL3` in place of the SDL files, it prints the clash that made the isolation necessary:
  `CompiledFilter`, `FramebufferData`, `ProgramId`, ....)
- The Variant helpers in `ShimGenerator.cs` (`VariantHelpers`) list `Rml::Variant`'s value types by name. A new
  value type still compiles (it crosses as its type only) but carries no value until it is added there and in
  `Api/Variant.cs`. Compare `Rml::Variant::Type` in `Variant.h` with the list.

**5. Recheck licenses.** `THIRD-PARTY-NOTICES.txt` is copied from the sources' own license files, including what
RmlUi bundles. Diff RmlUi's license files between the two commits and update the notices if anything changed.

**6. Run the whole [testing.md](testing.md) sequence**, and update the pinned commit where the docs name it
(README, [architecture.md](architecture.md)).

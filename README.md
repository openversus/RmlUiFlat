# ovs-ui-test

C# bindings for [RmlUi](https://github.com/mikke89/RmlUi), generated, NativeAOT-clean, statically linked. An
experiment toward an in-game menu for the OpenVersus client and possibly an installer.

## How it fits together

```
RmlUi's C++ headers
   │  gen/RmlShimGen (CppAst parse; rules in TypeMapper.cs; scope in gen/shim.json)
   ▼
native/generated/rmlui_c.h, rmlui_c.cpp   ← a C API over the C++ one
native/rmlui_c_manual.h, .cpp             ← hand-written: C++ interfaces RmlUi calls back into (EventListener)
   │  HexaGen's C-to-C# generator (gen/hexagen.json), run by the same RmlShimGen invocation
   ▼
src/RmlUi.Net/Generated/                  ← LibraryImport bindings
```

Nothing under `generated/` or `Generated/` is edited by hand. To change what is wrapped, edit `gen/shim.json`
and regenerate. `gen/shim-report.md` lists everything in scope that the generator could not map, grouped by
reason, and every RmlUi class not in scope: after an RmlUi update, its diff is the list of what changed shape.

How a C++ type crosses:

| C++ | C | Notes |
|---|---|---|
| `const String&`, `String` parameter | `const char*` (UTF-8) | null reads as empty |
| `const String&` return | `const char*` | owned by RmlUi, valid while its owner is |
| `String` return | `const char*` | per-thread buffer, valid until the next such call on the thread; the C# `...S` overloads copy it at once |
| class listed in `Classes`/`OpaqueTypes` | opaque handle pointer | `Rml{Name}_As{Base}` upcasts for handle bases |
| `ValueTypes` (Vector2f, Colourb, ...) | C struct, copied by `std::bit_cast` | size, alignment and field offsets `static_assert`ed against RmlUi's type |
| enum | C enum | every value `static_assert`ed against RmlUi's |
| `Span<const T>` (T primitive) | `const T*, size_t` | |
| `long` | `int64_t` | 32-bit on Windows, 64-bit on Linux |

Overloads are named from their C++ parameter types (`RmlGetContext_String`, `RmlGetContext_Int`), counting every
overload, so a new mapping rule never renames an existing function. `Renames` in shim.json gives nicer names.

## Regenerate

```sh
dotnet run --project gen/RmlShimGen            # reads gen/shim.json
```

Needs RmlUi at `external/RmlUi` and a libstdc++/clang include setup matching `SystemIncludeFolders` in shim.json
(the paths are this machine's; the parse target does not affect the ABI, which the static_asserts check on the
compiler that builds the shim).

## Build and run the demo (Linux)

```sh
cmake -S native -B native/build/linux -G Ninja -DCMAKE_BUILD_TYPE=Release -DRMLC_BACKEND=X11_GL2
cmake --build native/build/linux --target rmlui_c rmlui_c_backend
dotnet publish demo/Demo -c Release -r linux-x64 -o demo/Demo/bin/publish
demo/Demo/bin/publish/Demo external/RmlUi/Samples/assets              # a window
demo/Demo/bin/publish/Demo --selftest external/RmlUi/Samples/assets   # clicks the button, checks the result, saves a screenshot
```

`RMLC_BACKEND` builds one of RmlUi's sample desktop backends (window, input, renderer) as `rmlui_c_backend`, for
standalone windows. The in-game overlay will not use it; it needs a render interface for the game's own device.

## Not done yet

- Windows: nothing here has been cross-built for Windows or run under Proton.
- In-game: a DXGI Present hook and RmlUi's DX11 or DX12 renderer (which one depends on the game's device).
- Unmapped in scope (see shim-report.md): `ElementPtr` ownership (`CreateElement`, `AppendChild`), `Dictionary`
  and `Variant` (event parameters), `Property`, and the containers (`StringList`, `ElementList`).

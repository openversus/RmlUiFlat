# ovs-ui-test

C# bindings for [RmlUi](https://github.com/mikke89/RmlUi), generated, NativeAOT-clean, statically linked. An
experiment toward an in-game menu for the OpenVersus client and possibly an installer.

## Using it

```csharp
using RmlUi;

Rml.Initialise();
var context = Rml.CreateContext("main", new Vector2i(800, 480));
var document = context.LoadDocumentFromMemory(rml);
document.Show();

var status = document.GetElementById("status");
var listener = EventListener.Create(evt => status.SetInnerRML("clicked"));
document.GetElementById("hello").AddEventListener("click", listener);
```

The API mirrors RmlUi's C++: `Rml::` free functions are `Rml.*`, each class is a handle struct with the same
method names (C# overloads and C++ default arguments where they have a C# spelling), enums keep RmlUi's names
(`Style.FontWeight.Bold`, `Input.KeyIdentifier.KI_A`), and an `ElementDocument` converts to `Element` and has its
methods. Handles do not own what they point at; RmlUi's own lifetime rules apply, and the C++ doc comments,
including `@lifetime` notes, come through as XML docs.

### A standalone window

`src/RmlUi.Net.Desktop` (for an installer, tools, the demo; the in-game client does not reference it) wraps RmlUi's
desktop backend and owns the setup and teardown RmlUi is particular about:

```csharp
using RmlUi;
using RmlUi.Desktop;

using var window = new RmlWindowBuilder("Installer", 800, 480)
    .AddFont(regularBytes, "Lato")
    .AddFont(boldBytes, "Lato", weight: Style.FontWeight.Bold)
    .Build();

var document = window.Context.LoadDocumentFromMemory(rml);
document.Show();
window.Listen(document.GetElementById("install"), "click", e => StartInstall());
window.Run();
```

`RunFrame` and `AfterRender` are there for driving it from code. It needs an `rmlui_c` built with a backend
(`RMLC_BACKEND` on Linux, `cross/build-windows.sh Win32_DX11`).

### Hosts

What every host shares lives in `RmlUi.Hosting` (in RmlUi.Net), so the installer's window and the in-game overlay are
built on the same thing rather than one bent to fit the other:

- `RmlHost` initialises RmlUi, owns `Context`, `Listen`ed listeners and in-memory fonts, and disposes in RmlUi's
  order: `Rml::Shutdown` (destroying the context and detaching every listener), then the listeners, then the font
  memory RmlUi was reading, then the host's own resources. RmlUi is process-wide, so one host exists at a time; a
  second is refused.
- `RmlHostBuilder<TSelf>` holds the configuration every host takes (`AddFont`, `ContextName`); a host's builder
  derives from it and chains its own options.

A host brings up its window and renderer, calls `InitialiseRml`, and releases its own resources in `ShutdownHost`.
`RmlWindow` does that with RmlUi's desktop backend and adds the frame loop. The in-game overlay will do it with the
game's swap chain from a Present hook, the game's window messages, and RmlUi's DX11 or DX12 renderer.

## How it fits together

```
RmlUi's C++ headers
   │  gen/RmlShimGen (CppAst parse; rules in TypeMapper.cs; scope in gen/shim.json)
   ▼
native/generated/rmlui_c.h, rmlui_c.cpp   ← a C API over the C++ one
native/rmlui_c_manual.h, .cpp             ← hand-written: C++ interfaces RmlUi calls back into (EventListener)
   │  same RmlShimGen run
   ├─▶ src/RmlUi.Net/Api.Generated/       ← the C# API above (CsLayer.cs), LibraryImports of its own
   └─▶ src/RmlUi.Net/Generated/           ← HexaGen's raw bindings over the C API (namespace RmlUi.Interop)
src/RmlUi.Net/Api/                        ← hand-written C#: EventListener.Create, paired with rmlui_c_manual.cpp
```

Nothing under `generated/`, `Generated/` or `Api.Generated/` is edited by hand; the generator recreates those
directories. To change what is wrapped, edit `gen/shim.json` and regenerate. `gen/shim-report.md` lists everything
in scope that could not be mapped, grouped by reason, any method whose C# name had to differ from C++, and every
RmlUi class not in scope: after an RmlUi update, its diff is the list of what changed shape.

The C# API does not use HexaGen's raw layer; it declares its own imports from the same C header, so HexaGen's
naming rules never have to be predicted. The raw layer is there for anything the C# API does not cover yet.

How a C++ type crosses:

| C++ | C | C# | Notes |
|---|---|---|---|
| `const String&`, `String` parameter | `const char*` (UTF-8) | `string` | null reads as empty |
| `const String&` return | `const char*` | `string` (copied) | owned by RmlUi, valid while its owner is |
| `String` return | `const char*` | `string` (copied) | per-thread buffer in C, valid until the next such call on the thread |
| class in `Classes`/`OpaqueTypes` | opaque handle pointer | handle struct | `Rml{Name}_As{Base}` upcasts, implicit in C# |
| `ValueTypes` (Vector2f, Colourb, ...) | C struct, copied by `std::bit_cast` | struct of the same fields | size, alignment and field offsets `static_assert`ed against RmlUi's type |
| enum | C enum | `enum : int` | every value `static_assert`ed against RmlUi's |
| `Span<const T>` (T primitive) | `const T*, size_t` | `T*, nuint` | a pointer, not a span: RmlUi may keep it (LoadFontFace does, until Shutdown) |
| `bool` | `bool` | `bool` (one byte on the wire) | |
| `long` | `int64_t` | `long` | 32-bit on Windows, 64-bit on Linux |

C names for overloads come from their C++ parameter types (`RmlGetContext_String`, `RmlGetContext_Int`), counting
every overload, so a new mapping rule never renames an existing function. C# uses the C++ name and overloading.

Code style is ovs-client's `.editorconfig`; `Directory.Build.props` makes its rules build errors, and the generated
C# API is checked by them too (HexaGen's output is marked auto-generated and is not).

## Why not HexaGen.Cpp2C

HexaGen has a C++-to-C generator. Run on RmlUi Core (HexaGen 50547a0, 2026-09-26), it had no path for free functions
(`Rml::Initialise`, `CreateContext`, ...), no `std::string` handling (`String const*` in C, cast straight to
`std::string*`), no virtual methods, wrapped every class in libstdc++ (ignoring its allowed-header list), read CRTP
(`Element : EnableObserverPtr<Element>`) as a value cycle, declared `...Destroy` functions without defining them,
emitted `0xff` in a `uint8_t` enum as `-1`, copied classes with `std::string` members into C structs, and crashed on
an unhandled template parameter after four patches. It is a proof of concept for one project's headers. The parser
under it (HexaGen.CppAst) and the C-to-C# generator are what this repository uses, both from NuGet.

## Regenerate

```sh
dotnet run --project gen/RmlShimGen            # reads gen/shim.json
```

Needs RmlUi at `external/RmlUi` and a libstdc++/clang include setup matching `SystemIncludeFolders` in shim.json
(the paths are this machine's; the parse target does not affect the ABI, which the static_asserts check on the
compiler that builds the shim). HexaGen rewrites `gen/hexagen.json` when it loads it, dropping keys equal to its
defaults; that is expected. `DelegatesAsVoidPointer` is set in Program.cs because a `false` in the file does not
survive the load.

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

## Build for Windows (cross-compiled from Linux)

```sh
cross/build-windows.sh              # native/build/win/rmlui_c.dll: RmlUi + FreeType + the shim, for a client to load
cross/build-windows.sh Win32_DX11   # native/build/win-Win32_DX11/rmlui_c.dll: the same plus RmlUi's Win32/DX11 window backend
dotnet publish demo/Demo -c Release -r win-x64 -p:AcceptVSBuildToolsLicense=true -o demo/Demo/bin/publish-win
wine demo/Demo/bin/publish-win/Demo.exe --selftest
```

clang-cl and lld-link against an xwin splat (`cross/xwin-clang-cl.cmake`, found at `$XWIN_SPLAT`,
`~/.xwin-cache/splat` or `~/.cache/xwin/splat`). FreeType is built from `external/freetype` (2.14.3) without zlib,
png, bzip2, brotli or harfbuzz. The C and C++ runtimes are linked statically: the DLL imports only Windows system DLLs,
so players need no VC++ redistributable. The demo on Windows loads `rmlui_c.dll` from beside the executable at run
time, the way the client would; on Linux it links everything statically. Checked under Wine 11.13: the self-test
passes, and the window renders as it does on Linux. The .NET side cross-compiles with
`PublishAotCrossXWin`, as ovs-client does.

## Not done yet

- In-game: a DXGI Present hook and RmlUi's DX11 renderer (the game's default) and DX12 renderer (`-d3d12`), chosen
  from the device the game created.
- Unmapped in scope (see shim-report.md): `ElementPtr` ownership (`CreateElement`, `AppendChild`), `Dictionary`
  and `Variant` (event parameters), `Property`, and the containers (`StringList`, `ElementList`).

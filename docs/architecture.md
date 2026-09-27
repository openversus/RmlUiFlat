# Architecture

What the pieces are, which of them are generated and which are written by hand, and why each hand-written piece
exists. For the generator's internals, see [generator.md](generator.md); for using the result,
[using.md](using.md).

## The layers

```
RmlUi's C++ headers (external/RmlUi/Include)
   │  gen/RmlShimGen: parses them (HexaGen.CppAst), maps each type (TypeMapper.cs); scope in gen/shim.json
   ▼
native/generated/rmlui_c.h, rmlui_c.cpp   ← a C API over the C++ one                         (generated)
native/rmlui_c_manual.h, .cpp             ← C++ interfaces RmlUi calls back into (EventListener)   (hand-written)
native/backend/                           ← the standalone window: SDL3 + RmlUi's Vulkan/OpenGL renderers (hand-written)
   │  same RmlShimGen run
   ├─▶ src/RmlUi.Net/Api.Generated/       ← the C# API, namespace RmlUi                         (generated)
   └─▶ src/RmlUi.Net/Generated/           ← HexaGen's raw bindings over the C API, RmlUi.Interop (generated)
src/RmlUi.Net/Api/                        ← EventListener.Create, OwnedElement, Variant          (hand-written)
src/RmlUi.Net/Hosting/                    ← RmlHost, RmlHostBuilder: what every host shares      (hand-written)
src/RmlUi.Net.Desktop/                    ← RmlWindow, RmlWindowBuilder over native/backend      (hand-written)
```

The rule that shaped all of it: **no hand-maintained C.** RmlUi's API is large and moving; a hand-written C layer
would be half the API and always behind. So the C API is generated from RmlUi's own headers, and the C# API is
generated from the same parse, rather than from the C header, so it can use C++ names, overloads, default arguments
and doc comments. Hand-written code exists only where generation cannot do the job:

| Hand-written | Why it cannot be generated |
|---|---|
| `native/rmlui_c_manual.cpp` + `Api/EventListener.cs` | `Rml::EventListener` is an interface RmlUi calls *into*: a C++ class has to implement it and forward to C function pointers, and C# has to supply them. |
| `Api/OwnedElement.cs` | Ownership rules (attach once, release unattached ones before shutdown, report leftovers) are a design, not a type mapping. The generator maps `ElementPtr` to it. |
| `Api/Variant.cs` | The C# value type and its conversions. The generator maps `Rml::Variant` to it and writes the C side. |
| `Hosting/` | Setup and teardown order, fonts kept alive, one host per process. |
| `native/backend/` + `RmlUi.Net.Desktop` | A window and a renderer, from RmlUi's sample backends. |

`native/`, `Generated/` and `Api.Generated/` hold generated code under version control, so a build needs no
generator run and a regeneration shows up as a diff. Nothing in them is edited by hand; the generator recreates them.

## The native library

`native/CMakeLists.txt` builds `rmlui_c`: RmlUi, the generated shim and the hand-written one, plus FreeType on
Windows. Options:

| Option | Default | Effect |
|---|---|---|
| `RMLC_DESKTOP` | OFF | Adds the standalone window backend (`rmlui_c_backend`): SDL3 (static, from `external/SDL`), RmlUi's OpenGL 3 renderer, and stb_image for PNG and JPEG. SDL3 is trimmed to video, events, audio, gamepad (with HIDAPI) and file dialogs, and loads X11/Wayland, Vulkan, GL and the audio backends at run time. The in-game DLL does not include it. |
| `RMLC_DESKTOP_VULKAN` | ON | With `RMLC_DESKTOP`, adds RmlUi's Vulkan renderer. |
| `RMLC_SHARED` | OFF | Builds a shared library (the Windows DLL) instead of static archives. |

**Linux** links the shim, RmlUi and the backend statically into the NativeAOT executable (`DirectPInvoke` in the
demo's project); FreeType is the system's, linked dynamically.
**Windows** loads `rmlui_c.dll` from beside the executable at run time, the way the game client will:
`cross/build-windows.sh` builds it with clang-cl and lld-link against an xwin splat of the Windows SDK
(`cross/xwin-clang-cl.cmake`; the splat is found at `$XWIN_SPLAT`, `~/.xwin-cache/splat` or `~/.cache/xwin/splat`),
with the C and C++ runtimes linked statically, so the DLL imports only Windows system DLLs (KERNEL32 and USER32 for the
in-game build) and players need no redistributable. FreeType is built from `external/freetype` without zlib, png,
bzip2, brotli or harfbuzz. `cross/build-windows.sh desktop` builds the DLL with the window backend. The .NET side
cross-compiles to win-x64 with the `PublishAotCrossXWin` package, as ovs-client does.

## Hosts

RmlUi is process-wide and particular about order: interfaces before `Initialise`, fonts before documents, the
context destroyed by `Shutdown`, listeners and font memory freed only after it. `RmlUi.Hosting.RmlHost` owns that
order so no host gets it wrong:

- `InitialiseRml` installs the system and render interfaces, initialises RmlUi, loads the fonts, creates the context.
- `Listen` attaches a C# handler and keeps its listener until disposal.
- `Dispose` releases unattached `OwnedElement`s (RmlUi releases an element through an instancer that `Shutdown`
  destroys, so after it would crash), calls `Rml::Shutdown`, destroys the listeners, frees the font memory, then calls
  the derived host's `ShutdownHost`.
- A second host while one exists is refused.

`RmlHostBuilder<TSelf>` holds the options every host takes (`AddFont`, `ContextName`); a host's builder derives from
it and chains its own. `RmlWindow`/`RmlWindowBuilder` are the desktop host. The in-game overlay will be
`RmlOverlay : RmlHost` with `RmlOverlayBuilder : RmlHostBuilder<RmlOverlayBuilder>`, bringing the game's swap chain
(from a Present hook), its window messages, and RmlUi's DX11 or DX12 renderer (the game runs DX11 by default and DX12
with `-d3d12`, so the DLL carries both and picks from the device it finds). Both hosts build on the same base on
purpose: neither is built for one consumer and bent to fit the other.

## The desktop backend

`native/backend/rmlui_c_backend.cpp` is RmlUi's SDL sample backend behind a small C API (`rmlui_c_backend.h`), with
three additions.

**Renderer choice.** Vulkan where the machine can run it, OpenGL 3 otherwise; `RmlRenderer.Vulkan` or `OpenGL`
forces one and fails rather than falling back. RmlUi's Vulkan renderer cannot fail cleanly: its `Initialize` returns
true unconditionally and its checks compile out in release builds, so it crashes later instead. `ProbeVulkan` runs
first and checks what `RmlUi_Renderer_VK.cpp`'s `Initialize_*` functions will need: the loader, an instance at RmlUi's
API version, a device of the type RmlUi picks, a queue that can present, `VK_KHR_swapchain`, and a throwaway
`vkCreateDevice` with RmlUi's features. It mirrors RmlUi as of 3045e6e; after an RmlUi update, compare it with
`Initialize_Device` ([walkthroughs.md](walkthroughs.md#update-rmlui)).

**Both renderers in one binary.** RmlUi's sample renderers are meant to be linked one at a time, and
`RmlUi_Renderer_GL3.cpp` and `RmlUi_Renderer_VK.cpp` define different types under the same names
(`Gfx::FramebufferData`, `CompiledFilter`, `ProgramId`, ...). Linked together, the linker is entitled to keep either
copy of a same-named inline function. Under MSVC's ABI the Vulkan renderer ran the OpenGL renderer's vector code for
its framebuffers and crashed on its first frame; Linux happened to keep compatible copies. `gl3_renderer.cpp`
compiles the OpenGL renderer inside its own namespace (`rmlc_gl3`), using RmlUi's `RMLUI_GL3_CUSTOM_LOADER` hook for
the glad loader, and `gl3_renderer.h` is the only way in. The Vulkan renderer and `RmlUi_Platform_SDL.cpp` share no
names today (checked as of 3045e6e); that pairing is the one to recheck after an update.

**Images.** PNG and JPEG through stb_image (from `external/stb`) for both renderers (`image_loading_renderer.h`);
RmlUi's own loaders read only TGA.

The OpenGL 3 renderer composites in `EndFrame`, so the back buffer is complete only after it: `AfterRender` runs after
`EndFrame` and before the swap for that reason.

## Code style and docs

C# follows ovs-client's `.editorconfig`; `Directory.Build.props` runs its rules during the build, and they are errors
(braces on every body, for one). Every public type and member has an XML doc comment, and a missing one fails the
build (CS1591, set in `.editorconfig`). The generated C# API is held to both, so the generator's output is checked by
the same rules as hand-written code. HexaGen's raw layer is exempt from the doc rules: it is its output, and it
neither documents every member nor escapes the comments it copies.

## Why not HexaGen.Cpp2C

HexaGen has a C++-to-C generator. Run on RmlUi Core (HexaGen 50547a0, 2026-09-26), it had no path for free functions
(`Rml::Initialise`, `CreateContext`, ...), no `std::string` handling (`String const*` in C, cast straight to
`std::string*`), no virtual methods, wrapped every class in libstdc++ (ignoring its allowed-header list), read CRTP
(`Element : EnableObserverPtr<Element>`) as a value cycle, declared `...Destroy` functions without defining them,
emitted `0xff` in a `uint8_t` enum as `-1`, copied classes with `std::string` members into C structs, and crashed on
an unhandled template parameter after four patches. It is a proof of concept for one project's headers. The parser
under it (HexaGen.CppAst) and the C-to-C# generator are what this repository uses, both from NuGet.

## Known issues

- **OpenGL under Wine:** Wine 11.13 Staging's EGL-backed WGL offers SDL no pixel format for a window, so a forced
  OpenGL renderer fails there. The same request works on real Windows, so it is Wine's. Wine and Proton always have
  Vulkan, which works.
- **HexaGen quirks** (all handled): it needs the Linux libclang runtime packages or it crashes natively on the version
  mismatch; `DelegatesAsVoidPointer` must be set in code, because a JSON `false` does not survive `Load` (and HexaGen
  rewrites `gen/hexagen.json` when it loads it); its struct constructors for function-pointer fields do not compile
  (`GenerateConstructorsForStructs=false`); it cannot wrap a handle-pointer-pointer struct field, which is why handle
  lists are `void*` in C; it copies C comments into XML without escaping, which is why its output is exempt from the
  XML-doc checks.

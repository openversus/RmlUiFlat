# RmlUiFlat

C# bindings for [RmlUi](https://github.com/mikke89/RmlUi), the HTML/CSS-style UI library, generated from RmlUi's own
headers. NativeAOT-clean, with no hand-maintained C. Built for the OpenVersus client's in-game menu and a
standalone installer.

```csharp
using RmlUi;
using RmlUi.Desktop;

using var window = new RmlWindowBuilder("Installer", 800, 480)
    .AddFont(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "LatoLatin-Regular.ttf")), "Lato")
    .Build();

var document = window.Context.LoadDocumentFromMemory(rml);
document.Show();

var name = ElementFormControlInput.From(document.GetElementById("name"));
window.Listen(document.GetElementById("install"), "click", e => StartInstall(name.GetValue()));
window.Run();
```

The C# API is RmlUi's C++ API, with C# conventions where they fit: `Rml::` functions are `Rml.*`, classes are
handle structs with RmlUi's method names, enums keep RmlUi's names (`Style.FontWeight.Bold`), strings, lists and
dictionaries are ordinary C# types, and ownership is handled for you. Every method carries RmlUi's own doc comment.

## Documentation

| | |
|---|---|
| [Using RmlUi.Net](docs/using.md) | The C# API with examples: windows, documents, events, form input, attributes, elements made in code, and the rules and gotchas. |
| [Architecture](docs/architecture.md) | The pieces, what is generated and what is hand-written and why, hosts, the desktop backend and its renderers, the Windows build. |
| [The generator](docs/generator.md) | How RmlUi's headers become the C and C# APIs: the stages, names, how each type crosses, and `shim.json`. |
| [Walkthroughs](docs/walkthroughs.md) | Bring an RmlUi class into scope, wrap a function template, add a type rule, update RmlUi. |
| [Checking a change](docs/testing.md) | The build and test sequence, what the self-test proves, mutation checks, Windows DLL checks, diagnosing under Wine. |

## Status

- **Works** on Linux (Vulkan and OpenGL) and Windows (cross-built from Linux; checked under Wine 11.13 and, for the
  demo, on real Windows with Vulkan and OpenGL). The demo's self-test exercises the type rules end to end ([testing.md](docs/testing.md#what-the-self-test-covers)).
- **Mapped**: 304 RmlUi functions: contexts, documents, elements, events, the form controls, text, tabs, progress,
  boxes, properties, stylesheets, attributes and event parameters (`Variant`, `Dictionary`), and elements made in
  code (`OwnedElement`).
- **Not mapped, by choice so far** (`gen/shim-report.md` lists everything, with reasons): RmlUi's data models (data
  binding), touch input, instancers (C++ interfaces for custom elements), and style and render internals.
- **In progress: the in-game overlay.** RmlUi draws over a Direct3D 11 swap chain with the game's own device, with
  the game's device state swapped out around it and faults contained (checked under Wine with DXVK against a stand-in
  game, `demo/OverlayTest`). Not done yet: hooking a game's `Present` and window messages, and DX12.
- **Known issue**: forced OpenGL does not work under Wine (its WGL offers no pixel format); it works on real Windows,
  and Vulkan works under Wine.

## Building

```sh
git clone --recurse-submodules <this repository>

# Linux: native library with the window backend, then the demo
cmake -S native -B native/build/linux -G Ninja -DCMAKE_BUILD_TYPE=Release -DRMLC_DESKTOP=ON
cmake --build native/build/linux --target rmlui_c rmlui_c_backend SDL3-static
dotnet publish demo/Demo -c Release -r linux-x64 -o demo/Demo/bin/publish
demo/Demo/bin/publish/Demo                       # a window; --selftest runs the checks

# Windows, cross-compiled from Linux
cross/build-windows.sh                           # native/build/win/rmlui_c.dll: for a client to load
cross/build-windows.sh desktop                   # native/build/win-desktop/rmlui_c.dll: with the window backend
dotnet publish demo/Demo -c Release -r win-x64 -p:AcceptVSBuildToolsLicense=true -o demo/Demo/bin/publish-win

# After changing what is wrapped
dotnet run --project gen/RmlShimGen              # reads gen/shim.json
```

The generated C and C# are checked in, so building needs no generator run. [testing.md](docs/testing.md) has the full
sequence and what each step checks.

## Dependencies

Git submodules under `external/`, pinned (shallow, so a clone fetches only the pinned commit;
`git submodule update --init` in an existing clone):

| | Version | Used by |
|---|---|---|
| [RmlUi](https://github.com/mikke89/RmlUi) | 3045e6e (2026-09-13, 6.4-dev) | everything |
| [FreeType](https://gitlab.freedesktop.org/freetype/freetype) | VER-2-14-3 | Windows builds (Linux uses the system one) |
| [SDL](https://github.com/libsdl-org/SDL) | release-3.4.16 | the window backend |
| [stb](https://github.com/nothings/stb) | 2c980bb (stb_image 2.30) | the window backend and the overlay |

HexaGen, HexaGen.CppAst and HexaGen.Runtime come from NuGet. `THIRD-PARTY-NOTICES.txt` has the license of everything
that ends up in a binary built here, including what RmlUi and SDL bundle, and which binaries contain it; ship it with
them. Moving a submodule means checking its license text too ([walkthroughs.md](docs/walkthroughs.md#update-rmlui)).

## License

See [LICENSE](LICENSE).

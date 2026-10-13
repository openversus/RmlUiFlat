# Checking a change

The sequence to run after changing the generator, the config, the hand-written code or RmlUi itself, what the demo's
self-test proves, and how to know a test can fail. Every command here is run from the repository root.

## The sequence

```sh
# 1. Regenerate. With nothing changed, git status must show no difference: the output is deterministic.
dotnet run --project gen/RmlShimGen
git status --short
git diff gen/shim-report.md          # what changed shape: new unmapped reasons, new classes, renamed C# methods

# 2. Native, Linux (desktop backend included, for the demo).
cmake -S native -B native/build/linux -G Ninja -DCMAKE_BUILD_TYPE=Release -DRMLC_DESKTOP=ON
cmake --build native/build/linux --target rmlui_c rmlui_c_backend SDL3-static

# 3. C#, from clean. Delete the previous publish too: if this publish fails, the old Demo is otherwise still there,
#    and step 4 runs it and passes for code that never built. Check the publish succeeded before going on.
rm -rf demo/Demo/bin/publish src/RmlUi.Net/bin/Release src/RmlUi.Net/obj/Release \
       src/RmlUi.Net.Desktop/bin/Release src/RmlUi.Net.Desktop/obj/Release demo/Demo/bin/Release demo/Demo/obj/Release
dotnet publish demo/Demo -c Release -r linux-x64 -o demo/Demo/bin/publish && echo published

# 4. The self-test, on both renderers.
demo/Demo/bin/publish/Demo --selftest --expect-renderer=vulkan
demo/Demo/bin/publish/Demo --selftest --renderer=opengl --expect-renderer=opengl   # also writes demo-selftest.ppm

# 5. Windows: both DLLs, then the demo under Wine.
cross/build-windows.sh
cross/build-windows.sh desktop
dotnet publish demo/Demo -c Release -r win-x64 -p:AcceptVSBuildToolsLicense=true -o demo/Demo/bin/publish-win
wine demo/Demo/bin/publish-win/Demo.exe --selftest --expect-renderer=vulkan
```

**Look at the screenshot.** `demo-selftest.ppm` is the OpenGL back buffer at the end of the self-test (Linux only),
a plain PPM image most viewers open. The demo's text, and at the top right the assets panel with its two images
(a small alien and a yellow RmlUi page), must be there. A log line says what the code did, not what appeared on screen:
the window has been black while every log line said frames were being presented.

**Warnings are errors that matter.** The build turns style rules and missing XML docs into errors. A clean build
prints no warnings either; a new one is worth reading before it is ignored.

## Windows DLL checks

A function missing from `rmlui_c.dll` fails only when it is first called, so check the exports directly. Every
function the headers declare must be exported:

```sh
grep -h -oE '^RMLC_API [^(]* (Rml[A-Za-z0-9_]+)\(' native/generated/rmlui_c.h native/rmlui_c_manual.h \
    | sed -E 's/.* (Rml[A-Za-z0-9_]+)\(/\1/' | sort -u > /tmp/declared.txt
llvm-readobj --coff-exports native/build/win/rmlui_c.dll | awk '/Name: Rml/{print $2}' | sort -u \
    | comm -23 /tmp/declared.txt -                              # prints nothing when every one is exported
```

The in-game DLL must import only DLLs that ship with Windows (today KERNEL32, USER32, IMM32 and D3DCOMPILER_47), so
players need nothing installed:

```sh
llvm-readobj --coff-imports native/build/win/rmlui_c.dll | awk '/Name: .*\.dll/{print $2}' | sort -u
```

## What the self-test covers

`demo/Demo/Program.cs --selftest` drives the demo document through RmlUi's own input path and checks each result.
It exits non-zero and prints `FAIL:` for each check that fails:

| Check | Proves |
|---|---|
| The fonts load by path, `./ui/panel.rml` loads, its linked `panel.rcss` sets `#probe` to 123 px, and both `<img>`s get a size of the right shape | `Assets`: embedded resources by path, path cleanup, the link resolved against the document, a TGA through RmlUi's loader (`Seek`, `Tell`) and a PNG through stb (`Length`, `Read`); the paths asked for are recorded and checked |
| An asset source that throws for `ui/throws.rml` | the exception is reported once through `EventListener.UnhandledException`, never reaches RmlUi, and the document counts as missing |
| Building a second window while one is open throws | one host per process |
| A click at the button's centre reaches the C# handler, which writes the status text | input, events, listeners, strings both ways |
| `RemoveContext` of a missing and an existing context | a C `bool` reads back false and true |
| `ElementFormControlInput.From` of the input, the button and a null element | checked downcasts: the right type, and null otherwise |
| `QuerySelectorAll`, `GetActivePseudoClasses`, `ArePseudoClassesSet(["hover"])` and with a class that is not set | lists out, a list in, every item of it reaching RmlUi |
| `GetSelection(out, out, out)` after `SetSelectionRange(0, 4)` | `int*` and `String*` outputs |
| The click's `mouse_x`/`mouse_y` against where the test clicked | event parameters, `Variant` integers |
| A key press seen by a capture-phase listener as `KI_A` | key parameters and enum round trips |
| A custom event with int, string, float and bool parameters, and `GetParameter` of one and of a missing key | a dictionary into RmlUi and back, every entry, every kind; `GetParameter<String>` and its default |
| `SetAttribute("value", ...)`, `GetAttribute("id")`, a missing attribute, `"42"` as an int, a float attribute | `SetAttribute<Variant>`, `Variant*` and null, conversions |
| Created, attached, attached twice, disposed, removed and re-attached, and one dropped element | `OwnedElement`'s rules; the host releases and reports exactly the dropped one |
| `GetBox(0, out offset)` | a value-type output |
| `GetProperty("display").ToString()` | a `Property` handle and its `ToString` |
| `--expect-renderer` | the renderer choice |

## The overlay test

`demo/OverlayTest` draws RmlUi over a stand-in game's Direct3D 11 swap chain. Windows only; it runs under Wine with
DXVK:

```sh
cross/build-windows.sh                  # rmlui_c.dll with the overlay, and rmlc_testgame.dll
dotnet publish demo/OverlayTest -c Release -p:AcceptVSBuildToolsLicense=true -o demo/OverlayTest/bin/publish
wine demo/OverlayTest/bin/publish/OverlayTest.exe --selftest       # without --selftest: a window until closed
```

It runs a few frames and checks, reading the back buffer before each present:

| Check | Proves |
|---|---|
| A point inside the magenta panel's padding is magenta | the overlay drew, where the document says |
| A point outside the panel is the game's background colour | the game's pixels are untouched |
| After every frame, the game's render target, viewport, topology and blend state are what it left bound | the device state swap |
| With `Visible` false, the panel's point is the game's colour | hidden draws nothing |
| The overlay is not faulted | no exception inside RmlUi |

It saves `overlay-selftest.ppm`, the back buffer of the last shown frame: look at it.

Mutations run against it, each caught: removing the device state swap (the state check fails on every frame);
skipping the renderer's `EndFrame` (the panel is not drawn); ignoring `Visible` in C# (the panel is drawn while
hidden). And a planted access violation in the middle of a frame: the test app keeps running and exits normally, the
overlay reports `exception 0xC0000005 while rendering` once and turns itself off, and the game's device state is
intact on every frame.

## Can the test fail?

A test that passes the first time it runs may simply never have reached the condition it is meant to catch. Before
trusting a new check, break the thing it checks and watch it fail:

1. Change one thing: a line in `native/generated/rmlui_c.cpp`, the generator, or the hand-written C#.
2. Rebuild what it affects and run the self-test. It must fail, on the check you added.
3. Restore the file (regenerate for generated files, and confirm it is byte-identical to before) and rebuild.

Check that the change actually happened before reading the result: a pattern that matched nothing leaves the code as
it was and the test passing, which proves nothing. Mutations that have been run against the self-test, each caught:

| Change | Caught by |
|---|---|
| Remove the button's listener | the click check |
| `rmlui_dynamic_cast` → `static_cast` in a `From` | the button passes as a text input |
| Pass only the first item of a string list | `ArePseudoClassesSet(["hover", "no-such-class"])` returns true |
| Pass a null `String*` to `GetSelection` | the selected text comes back empty |
| Skip the host's release of unattached elements | nothing reported; the dropped element leaks |
| Do not check whether an owned element was already attached | attaching twice succeeds |
| Make `OwnedElement.Dispose` do nothing | the disposed element is reported as dropped |
| Release unattached elements after `Rml::Shutdown` | a segmentation fault: RmlUi releases through an instancer `Shutdown` destroyed |
| Keep only a dictionary's first entry | the custom event arrives with one parameter |
| Leave an `INT` variant's value empty | `mouse_x`/`mouse_y` read 0 |
| Send a `STRING` variant empty | the input's value is not set |
| Report a null `Variant*` as a `NONE` variant | the missing attribute is not null |
| Make `GetParameter` look up the wrong key | the parameter reads as the default |
| Disable the Vulkan preflight, run with `VK_LOADER_DEVICE_ID_FILTER=0xdead` | a crash inside RmlUi instead of the OpenGL fallback |
| File interface `Seek` refuses `SEEK_END` | the TGA image gets no size |
| File interface `Length` returns 0 | the fonts by path fail to load, and `Build` throws before the test starts |
| Asset path cleanup keeps `.` segments | `./ui/panel.rml` is not found: no probe, no images, the paths are missing |

## The renderer cases

Vulkan is preferred and OpenGL is the fallback. These environment variables reach each branch of the choice on
Linux (checked on NVIDIA):

| Run with | Expect |
|---|---|
| nothing | Vulkan |
| `--renderer=opengl` | OpenGL |
| `SDL_VULKAN_LIBRARY=/nonexistent` | OpenGL: no Vulkan loader |
| `VK_DRIVER_FILES=/dev/null` | OpenGL: no driver |
| `VK_LOADER_DEVICE_ID_FILTER=0xdead` | OpenGL: every device filtered out, the case only the preflight catches |
| `VK_DRIVER_FILES=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json` | Vulkan on lavapipe (CPU) |
| `--renderer=vulkan` where there is none | a managed exception, not a crash |

## Diagnosing a failure under Wine

- Redirect the demo's output to a file (`wine ... > out.txt 2> err.txt`), not a pipe: through a pipe its own output
  did not arrive while DXVK's did (seen every time on 2026-10-13 with Wine 11.13; a .NET single-file build printed
  through a pipe fine; the cause is not known). The output is CRLF: `tr -d '\r'` before grepping.
- RmlUi logs through `OutputDebugString`: run with `WINEDEBUG=+debugstr` to see it.
- `WINEDEBUG=+vulkan` shows every Vulkan call.
- For line numbers, build `cross/build-windows.sh desktop -DCMAKE_BUILD_TYPE=RelWithDebInfo` and resolve addresses
  with `llvm-symbolizer`.
- To find who ended the process, an `atexit` handler that prints `RtlCaptureStackBackTrace` with module names worked
  where `winedbg` would not start the process with arguments.
- When the same code works in one host and fails in another (RmlUi's own sample under Wine, and ours), the hosts'
  difference is where to look. That is how the two-renderer name clash ([architecture.md](architecture.md)) was
  found.

## Cross-build traps

- `CMAKE_*_FLAGS_INIT` only seeds a fresh cache, so the script deletes its build directories each run.
- llvm-rc needs the SDK's include paths; SDL needs the SDK's `winrt` directory.
- Library names are case-sensitive on Linux (`Shlwapi.lib`).
- GCC does not search the including file's includers' directories the way clang-cl does.

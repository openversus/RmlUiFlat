# Using RmlUi.Net

How to put an RmlUi document on screen from C# and work with it: the window, documents, elements, events, form
input, attributes, and elements made in code. For how the bindings are built, see [architecture.md](architecture.md)
and [generator.md](generator.md).

The API mirrors RmlUi's C++ one. If you know RmlUi, you know most of this. If you don't, RmlUi's own
documentation (<https://mikke89.github.io/RmlUiDoc/>) is the reference for RML (its HTML), RCSS (its CSS) and what
each method does. The XML docs on every generated method are RmlUi's own comments, including its `@lifetime` notes,
with the C++ signature under *Remarks*.

## The pieces

| Namespace | Assembly | What it is |
|---|---|---|
| `RmlUi` | RmlUi.Net | The generated API: `Rml.*` (RmlUi's free functions), one handle struct per RmlUi class (`Context`, `Element`, `ElementDocument`, `Event`, the form controls, ...), enums, value types. Plus the hand-written `EventListener`, `OwnedElement` and `Variant`. |
| `RmlUi.Hosting` | RmlUi.Net | `RmlHost` and `RmlHostBuilder<TSelf>`: what every host shares (setup, fonts, listeners, teardown in RmlUi's order). |
| `RmlUi.Desktop` | RmlUi.Net.Desktop | `RmlWindow` and `RmlWindowBuilder`: a standalone window (SDL3, Vulkan or OpenGL). For an installer, tools, the demo. |
| `RmlUi.Interop` | RmlUi.Net | HexaGen's raw bindings over the C API. Only for something the `RmlUi` namespace does not cover yet. |

The in-game client will use a host of its own (an overlay on the game's swap chain) built on `RmlUi.Hosting`, so
everything below except the window itself applies there too.

## A window

```csharp
using RmlUi;
using RmlUi.Desktop;

string assets = Path.Combine(AppContext.BaseDirectory, "assets");
using var window = new RmlWindowBuilder("Installer", 800, 480)
    .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Regular.ttf")), "Lato")
    .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Bold.ttf")), "Lato", weight: Style.FontWeight.Bold)
    .Build();

var document = window.Context.LoadDocumentFromMemory(rml);
document.Show();
window.Listen(document.GetElementById("install"), "click", e => StartInstall());
window.Run();
```

- **The native library** must be built with the desktop backend: `-DRMLC_DESKTOP=ON` on Linux,
  `cross/build-windows.sh desktop` for Windows ([testing.md](testing.md) has the commands). The publish folder is then
  self-contained (the executable, `rmlui_c.dll` on Windows, anything you copy in such as fonts, and
  `THIRD-PARTY-NOTICES.txt`), so it can be zipped and run anywhere.
- **Fonts** are either bytes (`AddFont(data, family, style, weight)`), which the host keeps in native memory until
  RmlUi shuts down, or a file path (`AddFont(path)`). RmlUi draws no text without one.
- **The size** is in the display's logical units; the context is sized in physical pixels, so on a display scaled to
  125% an 800-wide window has a 1000-wide context. RmlUi's `dp` unit follows the scale, so write sizes in `dp`.
- **The renderer** is Vulkan where the machine can run it and OpenGL 3 otherwise. `.Renderer(RmlRenderer.OpenGL)`
  forces one, and fails rather than falling back; `window.Renderer` says which was chosen.
- **The loop**: `Run()` runs until the window closes or `RequestExit()` is called. `RunFrame()` runs one frame and
  returns false once the window has closed, for driving it from your own loop. `PowerSave(true)` (the default) waits
  for input between frames; turn it off for animation. `AfterRender` fires once each frame is complete (with OpenGL,
  before it is shown, so the back buffer can still be read; RmlUi's Vulkan renderer presents as it finishes).
- **One host at a time.** RmlUi is process-wide; building a second while one is open throws
  `InvalidOperationException`.
- **Dispose** tears down in the order RmlUi needs: unattached elements (below), `Rml::Shutdown`, listeners, font
  memory, then the window. Every handle into RmlUi is invalid afterwards.

## Documents

```csharp
var document = window.Context.LoadDocumentFromMemory(rml);
document.Show();
// ...
document.Close();
```

`LoadDocument(path)` loads from a file. RmlUi opens it with a plain `fopen`, so **a relative path is relative to the
working directory, not to the executable**. A published app should build the path from `AppContext.BaseDirectory`,
as the font example above does:

```csharp
var menu = window.Context.LoadDocument(Path.Combine(AppContext.BaseDirectory, "ui", "menu.rml"));
```

The same goes for anything the document itself references (stylesheets, images).

## Finding elements

```csharp
Element install = document.GetElementById("install");
Element first = document.QuerySelector("ul.servers > li");
Element[] rows = document.QuerySelectorAll("ul.servers > li");
```

Lookups that find nothing return a null handle rather than throwing: check `IsNull`. `QuerySelectorAll`,
`GetElementsByTagName` and `GetElementsByClassName` return arrays, empty when nothing matches.

**Handles do not own anything.** An `Element` is a pointer to an element RmlUi owns; it stays valid as long as RmlUi
keeps the element (until it is removed from its document, or the document is closed) and must not be used after.
Copying a handle copies the pointer. Two handles are `==` when they point at the same element.

## Events

```csharp
window.Listen(button, "click", e =>
{
    var where = e.GetParameters();
    Console.WriteLine($"clicked at {where["mouse_x"].ToInt()}, {where["mouse_y"].ToInt()}");
});
```

`Listen` attaches a C# handler to an element for an RmlUi event type (`click`, `change`, `keydown`, `submit`,
`mouseover`, ...; RmlUi's documentation lists them) and keeps the listener alive until the host is disposed.
Without a host, `EventListener.Create(handler)` makes one you attach with `element.AddEventListener` and destroy
yourself with `Destroy()` once it is removed.

- **Parameters** (`e.GetParameters()`) are a dictionary of `Variant`s: `mouse_x`, `button`, `key_identifier`,
  `value`, `ctrl_key`, ... depending on the event.
- **One parameter as text**: `e.GetParameter("value", "")`, with the second argument returned when the event has
  no such parameter.
- **Which key**: `(Input.KeyIdentifier)e.GetParameters()["key_identifier"].ToInt()`.
- **Listening for keys document-wide needs the capture phase.** A focused text input handles most keys itself and
  stops them from bubbling, so a `keydown` listener on the document never sees keys typed into a text box. Listen in
  the capture phase, which reaches the document before the input:

  ```csharp
  window.Listen(document, "keydown", e =>
  {
      if ((Input.KeyIdentifier)e.GetParameters()["key_identifier"].ToInt() == Input.KeyIdentifier.KI_ESCAPE)
      {
          document.Hide();
      }
  }, inCapturePhase: true);
  ```
- **Exceptions** a handler throws cannot unwind into RmlUi's C++, so they are caught at the boundary and passed to
  `EventListener.UnhandledException`, which writes to stderr unless you point it at your logger:
  `EventListener.UnhandledException = e => logger.LogError(e, "UI handler failed");`.
- **Your own events**: `element.DispatchEvent("changed", new Dictionary<string, Variant> { ["index"] = 3 })`. The
  parameters arrive in the listener's `GetParameters()` as sent.

## Form input

RmlUi's form elements (`<input>`, `<select>`, `<textarea>`) are their own classes. `GetElementById` returns an
`Element`; `From` gives you the specific class, or a null handle when the element is something else:

```csharp
var name = ElementFormControlInput.From(document.GetElementById("name"));
string entered = name.GetValue();
name.SetValue("Player One");

var renderer = ElementFormControlSelect.From(document.GetElementById("renderer"));
int chosen = renderer.GetSelection();
```

`From` is checked (it uses RmlUi's own type information), so `ElementFormControlInput.From(button).IsNull` is true
rather than a crash later. Every form control converts back to `Element` implicitly and has `Element`'s methods.

`SetSelectionRange` only selects text in an input that has focus; call `Focus()` first.

## Attributes and values

RmlUi's values (`Rml::Variant`) are a C# `Variant`. You rarely name the type: it converts implicitly from `string`,
`int`, `long`, `float`, `double`, `bool`, `Vector2f` and `Colourb`, and reads back with `ToString`, `ToInt`,
`ToLong`, `ToFloat`, `ToDouble`, `ToBool`, `ToVector2f` and `ToColourb`, which convert between kinds (`"42"` reads as
42; a bool prints as `"1"` or `"0"`, as RmlUi writes it).

```csharp
input.SetAttribute("value", "Player One");
slider.SetAttribute("max", 10);

string id = input.GetAttribute("id")?.ToString() ?? "";          // null when the attribute is absent
int max = slider.GetAttribute("max")?.ToInt() ?? 100;
IReadOnlyDictionary<string, Variant> all = input.GetAttributes();

input.SetAttributes(new Dictionary<string, Variant> { ["placeholder"] = "Name", ["maxlength"] = 16 });
```

`Variant.Kind` says what RmlUi stored (`Variant.Type.STRING`, `INT`, `FLOAT`, ...). Attributes written in RML are
strings; ones you set from C# keep the kind you set.

Style properties are strings in and out:

```csharp
button.SetProperty("background-color", "#3b5bdb");
string display = button.GetProperty("display").ToString();      // "inline-block"
button.RemoveProperty("background-color");
button.SetClass("selected", true);
```

## Elements made in code

`CreateElement`, `CreateTextNode` and `Clone` return an `OwnedElement`: an element no document owns yet. Attaching
it hands it to RmlUi. The usual case does both at once, and you never hold the owned element:

```csharp
Element row = list.AppendChild(document.CreateElement("li"));
row.SetInnerRML("Server one");
row.SetClass("server", true);
```

If you need to set it up before attaching, hold it and use `.Element`. `CreateElement` returns `OwnedElement?`
(RmlUi returns null only if it cannot create the element at all), so a held one takes a `!`; attaching straight away,
as above, does not need one, and attaching a null throws `ArgumentNullException`:

```csharp
using var row = document.CreateElement("li")!;
row.Element.SetInnerRML("Server two");
list.AppendChild(row);        // RmlUi owns it now; Dispose does nothing
```

- **Attaching** is `AppendChild`, `InsertBefore`, `ReplaceChild`, `ElementFormControlSelect.Add` or
  `ElementTabSet.SetTab`/`SetPanel`. Attaching the same one twice throws `InvalidOperationException`.
- **Removing** (`RemoveChild`, the replaced element from `ReplaceChild`) gives you an `OwnedElement` back, which you
  can attach elsewhere or dispose.
- **Forgetting one** does not leak past the host: when the host is disposed it releases every owned element nothing
  attached or disposed, before RmlUi shuts down, and reports each by address through `OwnedElement.Leaked` (stderr
  unless you point it at your logger). An element dropped every time a menu opens still grows memory for the whole
  session, though; the report is how you find it.

`SetInnerRML` is often simpler than building elements one by one: `list.SetInnerRML("<li>one</li><li>two</li>")`.

## Without a host

The generated API is all of RmlUi's setup too, so `Rml.*` can be driven directly. A host does this for you, in the
right order, with the system and render interfaces RmlUi needs to draw anything; this is what it is made of:

```csharp
Rml.Initialise();
var context = Rml.CreateContext("main", new Vector2i(800, 480));
var document = context.LoadDocumentFromMemory(rml);
document.Show();

var status = document.GetElementById("status");
var listener = EventListener.Create(evt => status.SetInnerRML("clicked"));
document.GetElementById("hello").AddEventListener("click", listener);
```

## Rules worth knowing

- **One thread.** Call RmlUi from the thread that created the host. Nothing checks this; RmlUi is not thread-safe.
- **One host per process**, enforced.
- **Strings and lists are copies.** A `string`, array or dictionary you get back is yours; changing it changes
  nothing in RmlUi.
- **Null handles are values, not exceptions.** Check `IsNull` after a lookup or a `From`.
- **Something missing?** `gen/shim-report.md` lists every RmlUi function that is in scope but not mapped, and why,
  and every RmlUi class not in scope. [walkthroughs.md](walkthroughs.md) shows how to bring more in.

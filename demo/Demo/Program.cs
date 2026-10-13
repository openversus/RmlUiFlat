// Opens a window with RmlUi through the generated bindings; its fonts, a document, a style sheet and images are embedded
// resources loaded by path. With --selftest it checks those loaded, clicks the button through RmlUi's input path, checks
// what the C# handler wrote back, saves a screenshot, and exits nonzero on failure.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RmlUi;
using RmlUi.Desktop;

namespace Demo;

internal static unsafe partial class Program
{
    private const int Width = 800;
    private const int Height = 480;
    private const string ThrowingPath = "ui/throws.rml";

    private const string Document = """
        <rml>
        <head>
            <title>OpenVersus</title>
            <style>
                body { font-family: LatoLatin; font-size: 20dp; color: #e8e8f0; background-color: #1b1d2a;
                       width: 100%; height: 100%; padding: 40dp; box-sizing: border-box; }
                div, h1, p { display: block; }
                h1 { font-size: 36dp; font-weight: bold; color: #ffffff; margin-bottom: 16dp; }
                p { margin-bottom: 24dp; }
                button { display: inline-block; padding: 10dp 24dp; background-color: #3b5bdb; color: #ffffff; border-radius: 6dp; }
                button:hover { background-color: #4c6ef5; }
                button:active { background-color: #364fc7; }
                #status { margin-top: 24dp; color: #a5d8ff; }
                input { display: inline-block; width: 240dp; padding: 6dp 10dp; margin-bottom: 16dp; color: #ffffff;
                        background-color: #2a2d3e; border: 1dp #4c6ef5; border-radius: 4dp; }
            </style>
        </head>
        <body>
            <h1>RmlUi from C#</h1>
            <p>Generated bindings, NativeAOT, one static link.</p>
            <div><input type="text" id="name" value="OpenVersus"/></div>
            <button id="hello">Click me</button>
            <div id="status">Not clicked yet.</div>
            <div id="list"></div>
        </body>
        </rml>
        """;

    private static int Main(string[] args)
    {
        bool selfTest = args.Contains("--selftest");
        var renderer = Enum.Parse<RmlRenderer>(Option(args, "--renderer") ?? "auto", ignoreCase: true);
        string? expectRenderer = Option(args, "--expect-renderer");

        // The self-test asks first: it records every path RmlUi asks for, and throws for one, then passes the rest on.
        var requested = new List<string>();
        var builder = new RmlWindowBuilder("RmlUi from C#", Width, Height)
            .Renderer(renderer)
            .PowerSave(!selfTest);
        if (selfTest)
        {
            builder.Assets(path =>
            {
                requested.Add(path);
                return path == ThrowingPath ? throw new IOException("planted by the self-test") : null;
            });
        }
        using var window = builder
            .Assets(typeof(Program).Assembly)
            .AddFont("fonts/LatoLatin-Regular.ttf")
            .AddFont("fonts/LatoLatin-Bold.ttf")
            .Build();
        Console.WriteLine($"RmlUi {Rml.GetVersion()}, renderer {window.Renderer}, {window.Context.GetDimensions()} px");
        if (expectRenderer != null && !string.Equals(window.Renderer.ToString(), expectRenderer, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FAIL: expected renderer {expectRenderer}, got {window.Renderer}");
            return 1;
        }

        var document = window.Context.LoadDocumentFromMemory(Document, "[demo]");
        document.Show();
        var button = document.GetElementById("hello");
        var status = document.GetElementById("status");
        var name = ElementFormControlInput.From(document.GetElementById("name"));

        int clicks = 0;
        IReadOnlyDictionary<string, Variant>? clickParameters = null;
        window.Listen(button, "click", e =>
        {
            clicks++;
            clickParameters = e.GetParameters();
            status.SetInnerRML($"Clicked {clicks} time{(clicks == 1 ? "" : "s")} — handled in C#. Name: {name.GetValue()}");
        });

        if (!selfTest)
        {
            window.Run();
            return 0;
        }
        // The host releases elements nothing attached or disposed, and reports each: the self-test leaves one on purpose.
        var leaked = new List<string>();
        OwnedElement.Leaked = leaked.Add;
        int exitCode = SelfTest(window, document, button, name, status, () => clicks, () => clickParameters, requested);
        window.Dispose();
        Console.WriteLine($"released by the host: [{string.Join(", ", leaked)}]");
        if (leaked.Count != 1 || leaked[0] != "span#dropped")
        {
            Console.Error.WriteLine("FAIL: expected the host to release and report only span#dropped");
            exitCode = 1;
        }
        return exitCode;
    }

    /// <summary>
    /// Clicks the button through RmlUi's own input path, then checks what the handler wrote back and a C bool round trip,
    /// and on Linux saves a screenshot. Returns the exit code.
    /// </summary>
    private static int SelfTest(
        RmlWindow window, ElementDocument document, Element button, ElementFormControlInput name, Element status, Func<int> clicks,
        Func<IReadOnlyDictionary<string, Variant>?> clickParameters, List<string> requested)
    {
        int exitCode = 0;

        // Assets: a document by path (written "./" to go through the path cleanup), its linked style sheet, and two
        // images, a TGA through RmlUi's own loader (Seek and Tell) and a PNG through stb (Length and Read). An image
        // only gets a size once its bytes have been read and decoded. The fonts came the same way, or Build would
        // have thrown.
        var panel = window.Context.LoadDocument("./ui/panel.rml");
        float probeWidth = 0, tgaWidth = 0, tgaHeight = 0, pngWidth = 0, pngHeight = 0;
        if (!panel.IsNull)
        {
            panel.Show();
            window.RunFrame();
            probeWidth = panel.GetElementById("probe").GetOffsetWidth();
            var tga = panel.GetElementById("tga");
            var png = panel.GetElementById("png");
            (tgaWidth, tgaHeight) = (tga.GetOffsetWidth(), tga.GetOffsetHeight());
            (pngWidth, pngHeight) = (png.GetOffsetWidth(), png.GetOffsetHeight());
        }
        Console.WriteLine($"assets: probe {probeWidth} px wide, tga {tgaWidth}x{tgaHeight}, png {pngWidth}x{pngHeight}");
        Console.WriteLine($"assets asked for: {string.Join(", ", requested.Distinct())}");
        string[] expectedPaths = ["fonts/LatoLatin-Regular.ttf", "fonts/LatoLatin-Bold.ttf", "ui/panel.rml", "ui/panel.rcss",
            "images/alien_small.tga", "images/hello_world.png"];
        // alien_small.tga is 32x32 and hello_world.png 540x276: the shapes must survive whatever scale the layout applies.
        if (probeWidth != 123 || tgaWidth <= 0 || Math.Abs(tgaWidth - tgaHeight) > 1
            || pngHeight <= 0 || Math.Abs(pngWidth / pngHeight - 540f / 276f) > 0.02f || expectedPaths.Except(requested).Any())
        {
            Console.Error.WriteLine($"FAIL: assets (missing paths: [{string.Join(", ", expectedPaths.Except(requested))}])");
            exitCode = 1;
        }

        // A source that throws: the exception is reported, never reaches RmlUi, and the file counts as missing.
        var reported = new List<Exception>();
        var report = EventListener.UnhandledException;
        EventListener.UnhandledException = reported.Add;
        bool thrownDocumentLoaded = !window.Context.LoadDocument(ThrowingPath).IsNull;
        EventListener.UnhandledException = report;
        Console.WriteLine($"throwing source: document loaded {thrownDocumentLoaded}, reported [{string.Join(" | ", reported.Select(e => e.Message + " <- " + e.InnerException?.Message))}]");
        if (thrownDocumentLoaded || reported.Count != 1 || !reported[0].Message.Contains(ThrowingPath, StringComparison.Ordinal)
            || reported[0].InnerException is not IOException)
        {
            Console.Error.WriteLine("FAIL: a throwing asset source must be reported once and count as missing");
            exitCode = 1;
        }

        // RmlUi is process-wide: a second host must be refused while this one is open, and leave this one working.
        try
        {
            using var second = new RmlWindowBuilder("second", 100, 100).Build();
            Console.Error.WriteLine("FAIL: a second RmlWindow was built while one was open");
            exitCode = 1;
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"second host refused: {e.Message}");
        }

        for (int frame = 0; frame < 5; frame++)
        {
            window.RunFrame();
        }

        var offset = button.GetAbsoluteOffset(BoxArea.Border);
        int x = (int)(offset.X + button.GetOffsetWidth() / 2);
        int y = (int)(offset.Y + button.GetOffsetHeight() / 2);
        window.Context.ProcessMouseMove(x, y, 0);
        window.Context.ProcessMouseButtonDown(0, 0);
        window.Context.ProcessMouseButtonUp(0, 0);
        Console.WriteLine($"clicked at ({x}, {y})");

        // glReadPixels reads the OpenGL back buffer; there is no equivalent here for Vulkan.
        if (OperatingSystem.IsLinux() && window.Renderer == RmlRenderer.OpenGL)
        {
            var size = window.Context.GetDimensions();
            window.AfterRender += () => SaveScreenshot("demo-selftest.ppm", size.X, size.Y);
        }
        window.RunFrame();

        // C bool is one byte: false must read back false, and true true.
        Rml.CreateContext("scratch", new Vector2i(1, 1));
        bool removedMissing = Rml.RemoveContext("no-such-context");
        bool removedScratch = Rml.RemoveContext("scratch");
        Console.WriteLine($"RemoveContext: missing={removedMissing} existing={removedScratch}");
        if (removedMissing || !removedScratch)
        {
            Console.Error.WriteLine("FAIL: RemoveContext returned the wrong bool");
            exitCode = 1;
        }

        // Downcasts are checked: an element of another type, or no element, comes back null.
        bool buttonIsInput = !ElementFormControlInput.From(button).IsNull;
        bool nothingIsInput = !ElementFormControlInput.From(default(Element)).IsNull;
        if (buttonIsInput || nothingIsInput)
        {
            Console.Error.WriteLine($"FAIL: From accepted the wrong element (button: {buttonIsInput}, null: {nothingIsInput})");
            exitCode = 1;
        }

        // Lists come back as arrays and go in as any IEnumerable. The mouse is still over the button.
        Element[] found = document.QuerySelectorAll("input, button");
        string[] active = button.GetActivePseudoClasses();
        bool hovered = button.ArePseudoClassesSet(["hover"]);
        bool hoveredAndMissing = button.ArePseudoClassesSet(["hover", "no-such-class"]);
        Console.WriteLine($"QuerySelectorAll: {found.Length} elements; button pseudo-classes: [{string.Join(", ", active)}]");
        if (found.Length != 2 || found[0] != name || found[1] != button || !active.Contains("hover") || !hovered || hoveredAndMissing)
        {
            Console.Error.WriteLine($"FAIL: lists (hover: {hovered}, hover and a missing class: {hoveredAndMissing})");
            exitCode = 1;
        }

        // Out parameters: RmlUi's int* and String* outputs. RmlUi only selects in a focused input.
        name.Focus();
        name.SetSelectionRange(0, 4);
        name.GetSelection(out int selectionStart, out int selectionEnd, out string selected);
        Console.WriteLine($"selection: {selectionStart}..{selectionEnd} \"{selected}\"");
        if (selectionStart != 0 || selectionEnd != 4 || selected != "Open")
        {
            Console.Error.WriteLine("FAIL: expected the selection 0..4 \"Open\"");
            exitCode = 1;
        }

        // Event parameters: where the click landed, which key was pressed, and a dictionary sent with a custom event.
        var clicked = clickParameters();
        Console.WriteLine($"click parameters: {(clicked == null ? "none" : string.Join(", ", clicked.Select(p => $"{p.Key}={p.Value}")))}");
        if (clicked == null || clicked["mouse_x"].ToInt() != x || clicked["mouse_y"].ToInt() != y)
        {
            Console.Error.WriteLine($"FAIL: expected mouse_x={x} and mouse_y={y} in the click's parameters");
            exitCode = 1;
        }
        Input.KeyIdentifier? pressed = null;
        // Capture phase: the name box has focus, and a focused text input stops the keys it handles from bubbling.
        window.Listen(document, "keydown", e => pressed = (Input.KeyIdentifier)e.GetParameters()["key_identifier"].ToInt(), inCapturePhase: true);
        window.Context.ProcessKeyDown(Input.KeyIdentifier.KI_A, 0);
        IReadOnlyDictionary<string, Variant>? received = null;
        string? oneParameter = null, missingParameter = null;
        window.Listen(status, "custom", e =>
        {
            received = e.GetParameters();
            oneParameter = e.GetParameter("s", "default");          // GetParameter<String>
            missingParameter = e.GetParameter("no-such-parameter", "default");
        });
        status.DispatchEvent("custom", new Dictionary<string, Variant> { ["n"] = 7, ["s"] = "text", ["f"] = 1.5f, ["on"] = true });
        Console.WriteLine($"GetParameter: s={oneParameter}, missing={missingParameter}");
        Console.WriteLine($"key: {pressed}; custom: {(received == null ? "none" : string.Join(", ", received.Select(p => $"{p.Key}={p.Value} ({p.Value.Kind})")))}");
        if (pressed != Input.KeyIdentifier.KI_A || received == null || received.Count != 4
            || received["n"] != 7 || received["s"].ToString() != "text" || received["f"].ToDouble() != 1.5 || !received["on"].ToBool()
            || oneParameter != "text" || missingParameter != "default")
        {
            Console.Error.WriteLine("FAIL: expected KI_A, the custom event's four parameters as sent, and GetParameter's value and default");
            exitCode = 1;
        }

        // Attributes: set from any value (SetAttribute<Variant>), read back as a Variant, null when absent.
        name.SetAttribute("value", "Player One");
        button.SetAttribute("data-n", "42");
        button.SetAttribute("data-f", 2.5f);
        var id = name.GetAttribute("id");
        var missing = name.GetAttribute("no-such-attribute");
        var n = button.GetAttribute("data-n");
        var f = button.GetAttribute("data-f");
        var all = button.GetAttributes();
        Console.WriteLine($"attributes: value={name.GetValue()}, id={id}, missing={missing?.ToString() ?? "null"}, data-n={n?.ToInt()}, data-f={f} ({f?.Kind}), button has [{string.Join(", ", all.Keys)}]");
        if (name.GetValue() != "Player One" || id?.ToString() != "name" || missing != null || n?.ToInt() != 42
            || f?.Kind != Variant.Type.FLOAT || f?.ToDouble() != 2.5 || !all.ContainsKey("id") || !all.ContainsKey("data-n"))
        {
            Console.Error.WriteLine("FAIL: attributes");
            exitCode = 1;
        }

        // Owned elements. Created and attached in one go, as most code will:
        var list = document.GetElementById("list");
        Element first = list.AppendChild(document.CreateElement("div"));
        first.SetInnerRML("first");

        // Attaching twice throws rather than handing RmlUi the same element again.
        var twice = document.CreateElement("div")!;
        list.AppendChild(twice);
        bool attachedTwice = false;
        try
        {
            list.AppendChild(twice);
            attachedTwice = true;
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"second attach refused: {e.Message}");
        }

        // Removed comes back owned and can go back in; a disposed one is released now; one is left for the host.
        using (document.CreateElement("p"))
        {
        }
        var removed = list.RemoveChild(first);
        bool removedIsFirst = removed != null && removed.Element == first;
        list.AppendChild(removed);
        document.CreateElement("span")!.Element.SetId("dropped");
        Console.WriteLine($"list children: {list.GetNumChildren()}, removed was first: {removedIsFirst}");
        if (attachedTwice || !removedIsFirst || list.GetNumChildren() != 2 || list.GetChild(1) != first)
        {
            Console.Error.WriteLine($"FAIL: owned elements (attached twice: {attachedTwice})");
            exitCode = 1;
        }

        // A value-type output (Vector2f&, documented [out]).
        var box = button.GetBox(0, out Vector2f boxOffset);
        Console.WriteLine($"box 0 at {boxOffset}, {box.GetSize(BoxArea.Border)}");
        if (box.IsNull || box.GetSize(BoxArea.Border).X <= 0)
        {
            Console.Error.WriteLine("FAIL: expected the button's first box");
            exitCode = 1;
        }

        // A Property prints as RmlUi writes it.
        string display = button.GetProperty("display").ToString();
        Console.WriteLine($"button display: {display}");
        if (display != "inline-block")
        {
            Console.Error.WriteLine("FAIL: expected the button's display property to read inline-block");
            exitCode = 1;
        }

        string text = status.GetInnerRML();
        Console.WriteLine($"status: {text}");
        const string expected = "Clicked 1 time — handled in C#. Name: OpenVersus";
        if (clicks() != 1 || text != expected)
        {
            Console.Error.WriteLine($"FAIL: expected one click and \"{expected}\"");
            exitCode = 1;
        }
        return exitCode;
    }

    /// <summary>Reads the back buffer before it is presented and writes a binary PPM, top row first.</summary>
    private static void SaveScreenshot(string path, int width, int height)
    {
        const uint GL_RGB = 0x1907, GL_UNSIGNED_BYTE = 0x1401, GL_PACK_ALIGNMENT = 0x0D05;
        var pixels = new byte[width * height * 3];
        glPixelStorei(GL_PACK_ALIGNMENT, 1);
        fixed (byte* p = pixels)
        {
            glReadPixels(0, 0, width, height, GL_RGB, GL_UNSIGNED_BYTE, p);
        }
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        for (int row = height - 1; row >= 0; row--)
        {
            file.Write(pixels, row * width * 3, width * 3);
        }
        Console.WriteLine($"screenshot: {Path.GetFullPath(path)}");
    }

    private static string? Option(string[] args, string name) =>
        args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    [LibraryImport("libGL.so.1")]
    private static partial void glReadPixels(int x, int y, int width, int height, uint format, uint type, void* data);

    [LibraryImport("libGL.so.1")]
    private static partial void glPixelStorei(uint pname, int param);
}

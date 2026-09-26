// Opens a window with RmlUi through the generated bindings. With --selftest it clicks the button through RmlUi's input
// path, checks what the C# handler wrote back, saves a screenshot, and exits nonzero on failure.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RmlUi;

namespace Demo;

internal static unsafe partial class Program
{
    private const int Width = 800;
    private const int Height = 480;

    private const string Document = """
        <rml>
        <head>
            <title>OpenVersus</title>
            <style>
                body { font-family: Lato; font-size: 20dp; color: #e8e8f0; background-color: #1b1d2a;
                       width: 100%; height: 100%; padding: 40dp; box-sizing: border-box; }
                div, h1, p { display: block; }
                h1 { font-size: 36dp; font-weight: bold; color: #ffffff; margin-bottom: 16dp; }
                p { margin-bottom: 24dp; }
                button { display: inline-block; padding: 10dp 24dp; background-color: #3b5bdb; color: #ffffff; border-radius: 6dp; }
                button:hover { background-color: #4c6ef5; }
                button:active { background-color: #364fc7; }
                #status { margin-top: 24dp; color: #a5d8ff; }
            </style>
        </head>
        <body>
            <h1>RmlUi from C#</h1>
            <p>Generated bindings, NativeAOT, one static link.</p>
            <button id="hello">Click me</button>
            <div id="status">Not clicked yet.</div>
        </body>
        </rml>
        """;

    private static int Main(string[] args)
    {
        bool selfTest = args.Contains("--selftest");
        string assets = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../external/RmlUi/Samples/assets");

        if (!Backend.Initialize("RmlUi from C#", Width, Height, true))
        {
            Console.Error.WriteLine("Backend.Initialize failed");
            return 1;
        }
        Rml.SetSystemInterface(Backend.GetSystemInterface());
        Rml.SetRenderInterface(Backend.GetRenderInterface());
        Rml.Initialise();
        Console.WriteLine($"RmlUi {Rml.GetVersion()}");

        // Font data is not copied by RmlUi: it must outlive Rml.Shutdown, so it lives in native memory until then.
        var fonts = new List<nint>();
        foreach (var (file, weight) in new[] { ("LatoLatin-Regular.ttf", Style.FontWeight.Normal), ("LatoLatin-Bold.ttf", Style.FontWeight.Bold) })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(assets, file));
            byte* data = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(data, bytes.Length));
            fonts.Add((nint)data);
            if (!Rml.LoadFontFace(data, (nuint)bytes.Length, "Lato", Style.FontStyle.Normal, weight))
            {
                Console.Error.WriteLine($"LoadFontFace failed for {file}");
                return 1;
            }
        }

        var context = Rml.CreateContext("main", new Vector2i(Width, Height));
        var document = context.LoadDocumentFromMemory(Document, "[demo]");
        document.Show();
        var button = document.GetElementById("hello");
        var status = document.GetElementById("status");

        int clicks = 0;
        var listener = EventListener.Create(evt =>
        {
            clicks++;
            status.SetInnerRML($"Clicked {clicks} time{(clicks == 1 ? "" : "s")} — handled in C#.");
        });
        button.AddEventListener("click", listener);

        int exitCode = 0;
        int frame = 0;
        bool running = true;
        while (running)
        {
            running = Backend.ProcessEvents(context, !selfTest);
            if (selfTest && frame == 5)
            {
                // Through RmlUi's own input path: move onto the button, press, release.
                var offset = button.GetAbsoluteOffset(BoxArea.Border);
                int x = (int)(offset.X + button.GetOffsetWidth() / 2);
                int y = (int)(offset.Y + button.GetOffsetHeight() / 2);
                context.ProcessMouseMove(x, y, 0);
                context.ProcessMouseButtonDown(0, 0);
                context.ProcessMouseButtonUp(0, 0);
                Console.WriteLine($"clicked at ({x}, {y})");
            }
            context.Update();
            Backend.BeginFrame();
            context.Render();
            if (selfTest && frame == 10)
            {
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

                string text = status.GetInnerRML();
                Console.WriteLine($"status: {text}");
                const string expected = "Clicked 1 time — handled in C#.";
                if (clicks != 1 || text != expected)
                {
                    Console.Error.WriteLine($"FAIL: expected one click and \"{expected}\"");
                    exitCode = 1;
                }
                SaveScreenshot("demo-selftest.ppm");
                running = false;
            }
            Backend.PresentFrame();
            frame++;
        }

        button.RemoveEventListener("click", listener);
        listener.Destroy();
        Rml.Shutdown();
        foreach (var data in fonts)
        {
            NativeMemory.Free((void*)data);
        }
        Backend.Shutdown();
        return exitCode;
    }

    /// <summary>Reads the back buffer before it is presented and writes a binary PPM, top row first.</summary>
    private static void SaveScreenshot(string path)
    {
        const uint GL_RGB = 0x1907, GL_UNSIGNED_BYTE = 0x1401;
        var pixels = new byte[Width * Height * 3];
        fixed (byte* p = pixels)
        {
            glReadPixels(0, 0, Width, Height, GL_RGB, GL_UNSIGNED_BYTE, p);
        }
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n"));
        for (int row = Height - 1; row >= 0; row--)
        {
            file.Write(pixels, row * Width * 3, Width * 3);
        }
        Console.WriteLine($"screenshot: {Path.GetFullPath(path)}");
    }

    [LibraryImport("libGL.so.1")]
    private static partial void glReadPixels(int x, int y, int width, int height, uint format, uint type, void* data);
}

/// <summary>rmlui_c_backend.h: RmlUi's sample desktop backend, for standalone windows.</summary>
internal static unsafe partial class Backend
{
    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_Initialize", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Initialize(string windowName, int width, int height, [MarshalAs(UnmanagedType.U1)] bool allowResize);

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_Shutdown")]
    public static partial void Shutdown();

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_GetSystemInterface")]
    private static partial nint GetSystemInterfaceNative();

    public static SystemInterface GetSystemInterface() => new(GetSystemInterfaceNative());

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_GetRenderInterface")]
    private static partial nint GetRenderInterfaceNative();

    public static RenderInterface GetRenderInterface() => new(GetRenderInterfaceNative());

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_ProcessEvents")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool ProcessEventsNative(nint context, [MarshalAs(UnmanagedType.U1)] bool powerSave);

    public static bool ProcessEvents(Context context, bool powerSave) => ProcessEventsNative(context.Handle, powerSave);

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_BeginFrame")]
    public static partial void BeginFrame();

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_PresentFrame")]
    public static partial void PresentFrame();
}

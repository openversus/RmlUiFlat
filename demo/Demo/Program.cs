// Opens a window with RmlUi through the generated bindings. With --selftest it clicks the button through RmlUi's input
// path, checks what the C# handler wrote back, saves a screenshot, and exits nonzero on failure.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RmlUi.Net;
using static RmlUi.Net.RmlUi;

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

    private sealed class ClickState
    {
        public RmlElementPtr Status;
        public int Clicks;
    }

    private static int Main(string[] args)
    {
        bool selfTest = args.Contains("--selftest");
        string assets = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../../../external/RmlUi/Samples/assets");

        if (!Backend.Initialize("RmlUi from C#", Width, Height, true))
        {
            Console.Error.WriteLine("Backend.Initialize failed");
            return 1;
        }
        RmlSetSystemInterface(Backend.GetSystemInterface());
        RmlSetRenderInterface(Backend.GetRenderInterface());
        RmlInitialise();
        Console.WriteLine($"RmlUi {RmlGetVersionS()}");

        // Font data is not copied by RmlUi: it must outlive Rml::Shutdown, so it lives in native memory until then.
        var fonts = new List<(nint Data, nuint Length)>();
        foreach (var (file, weight) in new[] { ("LatoLatin-Regular.ttf", RmlStyleFontWeight.Normal), ("LatoLatin-Bold.ttf", RmlStyleFontWeight.Bold) })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(assets, file));
            byte* data = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(data, bytes.Length));
            fonts.Add(((nint)data, (nuint)bytes.Length));
            if (!RmlLoadFontFaceSpanStringFontStyleFontWeightBoolInt(data, (nuint)bytes.Length, "Lato", RmlStyleFontStyle.Normal, weight, false, 0))
            {
                Console.Error.WriteLine($"LoadFontFace failed for {file}");
                return 1;
            }
        }

        var context = RmlCreateContext("main", new RmlVector2I { X = Width, Y = Height }, default, default);
        var document = RmlContextLoadDocumentFromMemory(context, Document, "[demo]");
        RmlElementDocumentShow(document, RmlModalFlag.None, RmlFocusFlag.Auto, RmlScrollFlag.Auto);
        var root = RmlElementDocumentAsElement(document);
        var button = RmlElementGetElementById(root, "hello");
        var state = new ClickState { Status = RmlElementGetElementById(root, "status") };

        var handle = GCHandle.Alloc(state);
        var callbacks = new RmlEventListenerCallbacks
        {
            UserData = (void*)GCHandle.ToIntPtr(handle),
            ProcessEvent = &OnClick,
        };
        var listener = RmlEventListenerCreate(&callbacks);
        RmlElementAddEventListenerStringEventListenerPtrBool(button, "click", listener, false);

        int exitCode = 0;
        int frame = 0;
        bool running = true;
        while (running)
        {
            running = Backend.ProcessEvents(context, !selfTest);
            if (selfTest && frame == 5)
            {
                // Through RmlUi's own input path: move onto the button, press, release.
                var offset = RmlElementGetAbsoluteOffset(button, RmlBoxArea.Border);
                int x = (int)(offset.X + RmlElementGetOffsetWidth(button) / 2);
                int y = (int)(offset.Y + RmlElementGetOffsetHeight(button) / 2);
                RmlContextProcessMouseMove(context, x, y, 0);
                RmlContextProcessMouseButtonDown(context, 0, 0);
                RmlContextProcessMouseButtonUp(context, 0, 0);
                Console.WriteLine($"clicked at ({x}, {y})");
            }
            RmlContextUpdate(context);
            Backend.BeginFrame();
            RmlContextRender(context);
            if (selfTest && frame == 10)
            {
                // C bool is one byte: false must read back false, and true true.
                RmlCreateContext("scratch", new RmlVector2I { X = 1, Y = 1 }, default, default);
                bool removedMissing = RmlRemoveContext("no-such-context");
                bool removedScratch = RmlRemoveContext("scratch");
                Console.WriteLine($"RemoveContext: missing={removedMissing} existing={removedScratch}");
                if (removedMissing || !removedScratch)
                {
                    Console.Error.WriteLine("FAIL: RmlRemoveContext returned the wrong bool");
                    exitCode = 1;
                }

                string status = RmlElementGetInnerRMLS(state.Status);
                Console.WriteLine($"status: {status}");
                const string expected = "Clicked 1 time — handled in C#.";
                if (state.Clicks != 1 || status != expected)
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

        RmlElementRemoveEventListenerStringEventListenerPtrBool(button, "click", listener, false);
        RmlEventListenerDestroy(listener);
        handle.Free();
        RmlShutdown();
        foreach (var (data, _) in fonts)
        {
            NativeMemory.Free((void*)data);
        }
        Backend.Shutdown();
        return exitCode;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClick(void* userData, RmlEvent* evt)
    {
        var state = (ClickState)GCHandle.FromIntPtr((nint)userData).Target!;
        state.Clicks++;
        RmlElementSetInnerRML(state.Status, $"Clicked {state.Clicks} time{(state.Clicks == 1 ? "" : "s")} — handled in C#.");
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
    public static partial RmlSystemInterface* GetSystemInterface();

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_GetRenderInterface")]
    public static partial RmlRenderInterface* GetRenderInterface();

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_ProcessEvents")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ProcessEvents(RmlContext* context, [MarshalAs(UnmanagedType.U1)] bool powerSave);

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_BeginFrame")]
    public static partial void BeginFrame();

    [LibraryImport("rmlui_c", EntryPoint = "RmlBackend_PresentFrame")]
    public static partial void PresentFrame();
}

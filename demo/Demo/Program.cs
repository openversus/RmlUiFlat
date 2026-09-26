// Opens a window with RmlUi through the generated bindings. With --selftest it clicks the button through RmlUi's input
// path, checks what the C# handler wrote back, saves a screenshot, and exits nonzero on failure.
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

        using var window = new RmlWindowBuilder("RmlUi from C#", Width, Height)
            .PowerSave(!selfTest)
            .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Regular.ttf")), "Lato", weight: Style.FontWeight.Normal)
            .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Bold.ttf")), "Lato", weight: Style.FontWeight.Bold)
            .Build();
        Console.WriteLine($"RmlUi {Rml.GetVersion()}");

        var document = window.Context.LoadDocumentFromMemory(Document, "[demo]");
        document.Show();
        var button = document.GetElementById("hello");
        var status = document.GetElementById("status");

        int clicks = 0;
        window.Listen(button, "click", _ =>
        {
            clicks++;
            status.SetInnerRML($"Clicked {clicks} time{(clicks == 1 ? "" : "s")} — handled in C#.");
        });

        if (!selfTest)
        {
            window.Run();
            return 0;
        }
        return SelfTest(window, button, status, () => clicks);
    }

    /// <summary>
    /// Clicks the button through RmlUi's own input path, then checks what the handler wrote back and a C bool round trip,
    /// and on Linux saves a screenshot. Returns the exit code.
    /// </summary>
    private static int SelfTest(RmlWindow window, Element button, Element status, Func<int> clicks)
    {
        int exitCode = 0;

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

        if (OperatingSystem.IsLinux())
        {
            window.AfterRender += () => SaveScreenshot("demo-selftest.ppm");
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

        string text = status.GetInnerRML();
        Console.WriteLine($"status: {text}");
        const string expected = "Clicked 1 time — handled in C#.";
        if (clicks() != 1 || text != expected)
        {
            Console.Error.WriteLine($"FAIL: expected one click and \"{expected}\"");
            exitCode = 1;
        }
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

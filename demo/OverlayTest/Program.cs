// Draws RmlUi over a stand-in game (native/overlay/testgame): a window, a Direct3D 11 flip-model swap chain, a scene
// that changes every frame, and pipeline state the game leaves bound. With --selftest it runs a few frames and checks
// that the UI drew where it should, that the game's pixels elsewhere are untouched, that nothing is drawn while the
// overlay is hidden, and that the game's device state comes back intact; it saves overlay-selftest.ppm and exits
// non-zero on failure.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RmlUi;
using RmlUi.Overlay;

namespace OverlayTest;

internal static partial class Program
{
    private const int Width = 800;
    private const int Height = 480;

    // The panel is solid magenta at a known place (40..300, 40..160), so the test can find it in the back buffer.
    private const string Document = """
        <rml>
        <head>
            <style>
                body { font-family: Lato; font-size: 18px; color: #ffffff; width: 100%; height: 100%; }
                #panel { position: absolute; left: 40px; top: 40px; width: 260px; height: 120px; padding: 12px;
                         box-sizing: border-box; background-color: #ff00ff; }
                h1 { display: block; font-size: 24px; font-weight: bold; }
            </style>
        </head>
        <body>
            <div id="panel"><h1>RmlUi over D3D11</h1>A game's swap chain, the game's device.</div>
        </body>
        </rml>
        """;

    // Inside the panel's padding, where no text reaches; and well outside the panel.
    private const int PanelX = 294;
    private const int PanelY = 154;
    private const int OutsideX = 600;
    private const int OutsideY = 60;

    private static int Main(string[] args)
    {
        if (!TestGame.Create(Width, Height))
        {
            Console.Error.WriteLine("FAIL: the test game could not create its window, device or swap chain");
            return 1;
        }
        try
        {
            return Run(args.Contains("--selftest"));
        }
        finally
        {
            TestGame.Destroy();
        }
    }

    private static int Run(bool selfTest)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        using var overlay = new RmlOverlayBuilder()
            .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Regular.ttf")), "Lato")
            .AddFont(File.ReadAllBytes(Path.Combine(assets, "LatoLatin-Bold.ttf")), "Lato", weight: Style.FontWeight.Bold)
            .Visible()
            .BuildForD3D11(TestGame.GetDevice(), TestGame.GetWindow());
        overlay.Context.LoadDocumentFromMemory(Document, "[overlay test]").Show();
        nint swapChain = TestGame.GetSwapChain();

        if (!selfTest)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (TestGame.PumpMessages())
            {
                TestGame.Draw((float)clock.Elapsed.TotalSeconds);
                overlay.RenderFrame(swapChain);
                TestGame.Present();
            }
            return 0;
        }
        return SelfTest(overlay, swapChain);
    }

    private static int SelfTest(RmlOverlay overlay, nint swapChain)
    {
        int exitCode = 0;
        void Fail(string message)
        {
            Console.Error.WriteLine($"FAIL: {message}");
            exitCode = 1;
        }

        const int frames = 5;
        for (int frame = 0; frame < frames; frame++)
        {
            TestGame.PumpMessages();
            TestGame.Draw(frame / 60f);
            overlay.RenderFrame(swapChain);
            if (!TestGame.StateIntact())
            {
                Fail($"frame {frame}: the game's device state changed");
            }
            if (frame == frames - 1)
            {
                uint panel = Pixel(PanelX, PanelY);
                uint outside = Pixel(OutsideX, OutsideY);
                Console.WriteLine($"shown: panel pixel {panel:X8}, outside pixel {outside:X8}");
                if (!IsMagenta(panel))
                {
                    Fail("the panel is not drawn where it should be");
                }
                if (!IsGameBackground(outside))
                {
                    Fail("the game's pixels outside the panel changed");
                }
                string shot = Path.GetFullPath("overlay-selftest.ppm");
                Console.WriteLine(TestGame.SaveScreenshot(shot) ? $"screenshot: {shot}" : "screenshot failed");
            }
            TestGame.Present();
        }

        // Hidden: RenderFrame must draw nothing.
        overlay.Visible = false;
        TestGame.Draw(frames / 60f);
        overlay.RenderFrame(swapChain);
        uint hidden = Pixel(PanelX, PanelY);
        Console.WriteLine($"hidden: panel pixel {hidden:X8}");
        if (!IsGameBackground(hidden))
        {
            Fail("the panel was drawn while the overlay was hidden");
        }
        TestGame.Present();

        Console.WriteLine($"faulted: {overlay.IsFaulted}");
        if (overlay.IsFaulted)
        {
            Fail("the overlay faulted");
        }
        return exitCode;
    }

    private static uint Pixel(int x, int y) => TestGame.ReadPixel(x, y, out uint rgba) ? rgba : 0;

    private static bool IsMagenta(uint rgba) => (rgba >> 24) > 240 && ((rgba >> 16) & 0xFF) < 16 && ((rgba >> 8) & 0xFF) > 240;

    // TestGame_Draw's background: red 0.1 and blue 0.3 at every time (green varies).
    private static bool IsGameBackground(uint rgba) =>
        Math.Abs((int)(rgba >> 24) - 26) <= 2 && Math.Abs((int)((rgba >> 8) & 0xFF) - 77) <= 2;

    /// <summary>native/overlay/testgame: the stand-in game.</summary>
    private static partial class TestGame
    {
        private const string Library = "rmlc_testgame";

        [LibraryImport(Library, EntryPoint = "TestGame_Create")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool Create(int width, int height);

        [LibraryImport(Library, EntryPoint = "TestGame_GetDevice")]
        public static partial nint GetDevice();

        [LibraryImport(Library, EntryPoint = "TestGame_GetSwapChain")]
        public static partial nint GetSwapChain();

        [LibraryImport(Library, EntryPoint = "TestGame_GetWindow")]
        public static partial nint GetWindow();

        [LibraryImport(Library, EntryPoint = "TestGame_PumpMessages")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool PumpMessages();

        [LibraryImport(Library, EntryPoint = "TestGame_Draw")]
        public static partial void Draw(float seconds);

        [LibraryImport(Library, EntryPoint = "TestGame_StateIntact")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool StateIntact();

        [LibraryImport(Library, EntryPoint = "TestGame_ReadPixel")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool ReadPixel(int x, int y, out uint rgba);

        [LibraryImport(Library, EntryPoint = "TestGame_SaveScreenshot", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool SaveScreenshot(string path);

        [LibraryImport(Library, EntryPoint = "TestGame_Present")]
        public static partial void Present();

        [LibraryImport(Library, EntryPoint = "TestGame_Destroy")]
        public static partial void Destroy();
    }
}

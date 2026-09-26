using System.Runtime.InteropServices;

namespace RmlUi.Desktop;

/// <summary>
/// A window with an RmlUi context in it, on RmlUi's desktop backend. Owns everything RmlUi needs kept alive and tears it
/// down in the order RmlUi requires. RmlUi and its backend are process-wide, so one window can be open at a time.
/// Build one with <see cref="RmlWindowBuilder"/>.
/// </summary>
public sealed unsafe class RmlWindow : IDisposable
{
    internal sealed record FontSource(string? Path, byte[]? Data, string? Family, Style.FontStyle Style, Style.FontWeight Weight, bool Fallback);

    private static int open;

    private readonly List<nint> fontMemory = [];
    private readonly List<EventListener> listeners = [];
    private bool backendUp;
    private bool rmlUp;
    private bool disposed;

    internal RmlWindow(string title, int width, int height, bool resizable, bool powerSave, string contextName, List<FontSource> fonts)
    {
        if (Interlocked.Exchange(ref open, 1) != 0)
        {
            throw new InvalidOperationException("An RmlWindow is already open; RmlUi allows one per process.");
        }
        try
        {
            if (!NativeBackend.Initialize(title, width, height, resizable))
            {
                throw new InvalidOperationException("RmlUi's desktop backend could not open a window.");
            }
            backendUp = true;

            Rml.SetSystemInterface(new SystemInterface(NativeBackend.GetSystemInterface()));
            Rml.SetRenderInterface(new RenderInterface(NativeBackend.GetRenderInterface()));
            if (!Rml.Initialise())
            {
                throw new InvalidOperationException("Rml::Initialise failed.");
            }
            rmlUp = true;

            foreach (var font in fonts)
            {
                LoadFont(font);
            }

            Context = Rml.CreateContext(contextName, new Vector2i(width, height));
            if (Context.IsNull)
            {
                throw new InvalidOperationException($"Rml::CreateContext(\"{contextName}\") failed.");
            }
            PowerSave = powerSave;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The window's RmlUi context: load documents into it.</summary>
    public Context Context { get; }

    /// <summary>Whether each frame waits for input before running.</summary>
    public bool PowerSave { get; set; }

    /// <summary>Raised each frame after the context is rendered and before it is presented.</summary>
    public event Action? AfterRender;

    /// <summary>
    /// Attaches <paramref name="handler"/> to <paramref name="element"/> for <paramref name="eventType"/>. The window owns
    /// the listener and destroys it when disposed, after RmlUi has let go of it.
    /// </summary>
    public EventListener Listen(Element element, string eventType, Action<Event> handler, bool inCapturePhase = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (element.IsNull)
        {
            throw new ArgumentException("Element is null; check the id it was looked up by.", nameof(element));
        }
        var listener = EventListener.Create(handler);
        listeners.Add(listener);
        element.AddEventListener(eventType, listener, inCapturePhase);
        return listener;
    }

    /// <summary>Runs one frame: input, update, render, present. Returns false once the window has been closed.</summary>
    public bool RunFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!NativeBackend.ProcessEvents(Context.Handle, PowerSave))
        {
            return false;
        }
        Context.Update();
        NativeBackend.BeginFrame();
        Context.Render();
        AfterRender?.Invoke();
        NativeBackend.PresentFrame();
        return true;
    }

    /// <summary>Runs frames until the window is closed or <see cref="RequestExit"/> is called.</summary>
    public void Run()
    {
        while (RunFrame())
        {
        }
    }

    /// <summary>Asks the window to close; <see cref="RunFrame"/> then returns false.</summary>
    public void RequestExit() => NativeBackend.RequestExit();

    /// <summary>
    /// Shuts RmlUi down (which destroys the context and detaches every listener), then destroys the listeners, frees
    /// the font memory RmlUi was reading, and closes the window. Handles into RmlUi are invalid afterwards.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (rmlUp)
        {
            Rml.Shutdown();
        }
        foreach (var listener in listeners)
        {
            listener.Destroy();
        }
        listeners.Clear();
        foreach (var memory in fontMemory)
        {
            NativeMemory.Free((void*)memory);
        }
        fontMemory.Clear();
        if (backendUp)
        {
            NativeBackend.Shutdown();
        }
        Volatile.Write(ref open, 0);
    }

    private void LoadFont(FontSource font)
    {
        if (font.Path != null)
        {
            if (!Rml.LoadFontFace(font.Path, font.Fallback))
            {
                throw new InvalidOperationException($"RmlUi could not load the font file {font.Path}.");
            }
            return;
        }

        // RmlUi keeps this pointer until Shutdown, so it lives in native memory owned by the window.
        var data = font.Data!;
        byte* memory = (byte*)NativeMemory.Alloc((nuint)data.Length);
        fontMemory.Add((nint)memory);
        data.CopyTo(new Span<byte>(memory, data.Length));
        if (!Rml.LoadFontFace(memory, (nuint)data.Length, font.Family!, font.Style, font.Weight, font.Fallback))
        {
            throw new InvalidOperationException($"RmlUi could not load the font data for family {font.Family}.");
        }
    }
}

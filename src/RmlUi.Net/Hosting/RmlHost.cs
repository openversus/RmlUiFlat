using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace RmlUi.Hosting;

/// <summary>
/// What every RmlUi host has in common, whatever supplies the window, renderer and frame loop (a desktop window, an
/// in-game overlay): it initialises RmlUi, owns what RmlUi needs kept alive, and tears down in the order RmlUi requires.
/// RmlUi is process-wide, so one host can exist at a time.
/// </summary>
/// <remarks>
/// A derived host brings up its window and renderer, calls <see cref="InitialiseRml"/>, and releases its own
/// resources in <see cref="ShutdownHost"/>, which runs after RmlUi has been shut down.
/// </remarks>
public abstract unsafe class RmlHost : IDisposable
{
    private static int live;

    private readonly List<nint> fontMemory = [];
    private readonly List<EventListener> listeners = [];
    private bool rmlUp;
    private bool disposed;

    /// <exception cref="InvalidOperationException">Another host exists.</exception>
    protected RmlHost()
    {
        if (Interlocked.Exchange(ref live, 1) != 0)
        {
            throw new InvalidOperationException("An RmlUi host already exists; RmlUi allows one per process.");
        }
    }

    /// <summary>The host's RmlUi context: load documents into it.</summary>
    public Context Context { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    protected bool IsDisposed => disposed;

    /// <summary>
    /// Attaches <paramref name="handler"/> to <paramref name="element"/> for <paramref name="eventType"/>. The host owns
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

    /// <summary>
    /// Installs the interfaces, initialises RmlUi, loads the fonts and creates the context. Call once the host's window
    /// and renderer exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">A step fails. The caller should dispose the host.</exception>
    protected void InitialiseRml(RmlHostSettings settings, SystemInterface systemInterface, RenderInterface renderInterface, Vector2i dimensions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Rml.SetSystemInterface(systemInterface);
        Rml.SetRenderInterface(renderInterface);
        if (!Rml.Initialise())
        {
            throw new InvalidOperationException("Rml::Initialise failed.");
        }
        rmlUp = true;

        foreach (var font in settings.Fonts)
        {
            LoadFont(font);
        }

        Context = Rml.CreateContext(settings.ContextName, dimensions);
        if (Context.IsNull)
        {
            throw new InvalidOperationException($"Rml::CreateContext(\"{settings.ContextName}\") failed.");
        }
    }

    /// <summary>Releases the host's own resources (window, renderer, hooks). Runs after RmlUi is shut down.</summary>
    protected abstract void ShutdownHost();

    /// <summary>
    /// Shuts RmlUi down (which destroys the context and detaches every listener), then destroys the listeners, frees
    /// the font memory RmlUi was reading, and lets the host release its own resources. Handles into RmlUi are invalid
    /// afterwards.
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
        ShutdownHost();
        Volatile.Write(ref live, 0);
        GC.SuppressFinalize(this);
    }

    private void LoadFont(RmlHostSettings.Font font)
    {
        if (font.Path != null)
        {
            if (!Rml.LoadFontFace(font.Path, font.Fallback))
            {
                throw new InvalidOperationException($"RmlUi could not load the font file {font.Path}.");
            }
            return;
        }

        // RmlUi keeps this pointer until Shutdown, so it lives in native memory owned by the host.
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

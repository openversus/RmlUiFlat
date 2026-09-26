using RmlUi.Hosting;

namespace RmlUi.Desktop;

/// <summary>
/// A window with an RmlUi context in it, on RmlUi's desktop backend. Font memory, listeners and teardown order are
/// <see cref="RmlHost"/>'s; this adds the window, the backend's renderer and the frame loop. Build one with
/// <see cref="RmlWindowBuilder"/>.
/// </summary>
public sealed class RmlWindow : RmlHost
{
    private bool backendUp;

    internal RmlWindow(RmlHostSettings settings, string title, int width, int height, bool resizable, bool powerSave)
    {
        try
        {
            if (!NativeBackend.Initialize(title, width, height, resizable))
            {
                throw new InvalidOperationException("RmlUi's desktop backend could not open a window.");
            }
            backendUp = true;
            InitialiseRml(settings,
                new SystemInterface(NativeBackend.GetSystemInterface()),
                new RenderInterface(NativeBackend.GetRenderInterface()),
                new Vector2i(width, height));
            PowerSave = powerSave;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether each frame waits for input before running.</summary>
    public bool PowerSave { get; set; }

    /// <summary>Raised each frame after the context is rendered and before it is presented.</summary>
    public event Action? AfterRender;

    /// <summary>Runs one frame: input, update, render, present. Returns false once the window has been closed.</summary>
    public bool RunFrame()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
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

    /// <summary>Closes the window; RmlUi is already shut down by the time this runs.</summary>
    protected override void ShutdownHost()
    {
        if (backendUp)
        {
            NativeBackend.Shutdown();
            backendUp = false;
        }
    }
}

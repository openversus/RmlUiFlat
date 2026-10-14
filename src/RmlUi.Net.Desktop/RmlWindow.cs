using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RmlUi.Hosting;

namespace RmlUi.Desktop;

/// <summary>
/// A window with an RmlUi context in it, on RmlUi's desktop backend. Font memory, listeners and teardown order are
/// <see cref="RmlHost"/>'s; this adds the window, the backend's renderer and the frame loop. Build one with
/// <see cref="RmlWindowBuilder"/>.
/// </summary>
public sealed unsafe class RmlWindow : RmlHost
{
    private bool backendUp;

    internal RmlWindow(RmlHostSettings settings, string title, int width, int height, bool resizable, bool powerSave, RmlRenderer renderer)
    {
        try
        {
            if (!NativeBackend.Initialize(title, width, height, resizable, renderer))
            {
                throw new InvalidOperationException(renderer == RmlRenderer.Auto
                    ? "RmlUi's desktop backend could not open a window with Vulkan or OpenGL."
                    : $"RmlUi's desktop backend could not open a window with {renderer}.");
            }
            backendUp = true;
            Renderer = NativeBackend.GetRenderer();

            // The context works in physical pixels; RmlUi's dp unit scales by the display, as the platform layer does
            // again when the window moves to a display with another scale.
            int pixelWidth, pixelHeight;
            NativeBackend.GetPixelSize(&pixelWidth, &pixelHeight);
            InitialiseRml(settings,
                new SystemInterface(NativeBackend.GetSystemInterface()),
                new RenderInterface(NativeBackend.GetRenderInterface()),
                new Vector2i(pixelWidth, pixelHeight));
            Context.SetDensityIndependentPixelRatio(NativeBackend.GetDisplayScale());
            PowerSave = powerSave;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The renderer in use: <see cref="RmlRenderer.Vulkan"/> or <see cref="RmlRenderer.OpenGL"/>.</summary>
    public RmlRenderer Renderer { get; }

    /// <summary>Whether each frame waits for input before running.</summary>
    public bool PowerSave { get; set; }

    /// <summary>
    /// Raised each frame once the frame is complete in the back buffer. With OpenGL that is before it is shown, so the
    /// back buffer can be read; RmlUi's Vulkan renderer presents as it completes the frame.
    /// </summary>
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
        NativeBackend.EndFrame();
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
    /// Asks the person for a folder with the system's folder picker, over this window, starting in
    /// <paramref name="startIn"/> (null: where the system chooses). Returns at once; the task gets the folder chosen, or
    /// null when they cancelled, and fails with <see cref="InvalidOperationException"/> when no picker could be shown.
    /// Call it on the window's thread. On Linux the answer can arrive only while frames run, so run them without power
    /// save until the task completes; its continuations never run inside a frame.
    /// </summary>
    public Task<string?> ChooseFolderAsync(string? startIn = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeBackend.ShowOpenFolderDialog(&FolderChosen, GCHandle.ToIntPtr(GCHandle.Alloc(answer)), startIn);
        return answer.Task;
    }

    // The picker's answer, on whichever thread the system gives it; the strings are copied before it returns.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FolderChosen(nint userdata, byte* folder, byte* error)
    {
        var handle = GCHandle.FromIntPtr(userdata);
        var answer = (TaskCompletionSource<string?>)handle.Target!;
        handle.Free();
        if (error != null)
        {
            answer.TrySetException(new InvalidOperationException($"The folder picker could not be shown: {Marshal.PtrToStringUTF8((nint)error)}"));
        }
        else
        {
            answer.TrySetResult(folder == null ? null : Marshal.PtrToStringUTF8((nint)folder));
        }
    }

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

using RmlUi.Hosting;

namespace RmlUi.Overlay;

/// <summary>
/// An RmlUi context drawn over a game's Direct3D 11 swap chain, with the game's own device. Font memory, listeners and
/// teardown order are <see cref="RmlHost"/>'s; this adds RmlUi's DX11 renderer and draws into the back buffer just
/// before the game presents. Build one with <see cref="RmlOverlayBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything RmlUi does happens on the thread that presents (the game's render thread). While <see cref="Visible"/>
/// is false, <see cref="RenderFrame"/> returns at once and none of RmlUi runs.
/// </para>
/// <para>
/// An exception inside RmlUi or its renderer (an access violation, say) does not take the game down: the native side
/// catches it, restores the game's device state and disables the overlay for good. <see cref="Error"/> reports it, and
/// <see cref="IsFaulted"/> turns true.
/// </para>
/// </remarks>
public sealed class RmlOverlay : RmlHost
{
    private bool overlayUp;
    private string lastError = "";

    internal RmlOverlay(RmlHostSettings settings, nint device, nint window, bool visible)
    {
        try
        {
            if (!NativeOverlay.InitializeD3D11(device, window))
            {
                throw new InvalidOperationException($"RmlUi's overlay could not start: {NativeOverlay.Error}");
            }
            overlayUp = true;
            // The context takes the back buffer's size at the first frame; the client area is the best guess before it.
            NativeOverlay.GetClientRect(window, out var client);
            var size = new Vector2i(Math.Max(1, client.Right - client.Left), Math.Max(1, client.Bottom - client.Top));
            InitialiseRml(settings,
                new SystemInterface(NativeOverlay.GetSystemInterface()),
                new RenderInterface(NativeOverlay.GetRenderInterface()),
                size);
            Visible = visible;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Called with what went wrong when the overlay cannot draw (a back buffer it cannot use) or has been disabled by a
    /// fault; once for each new message. By default it is written to stderr.
    /// </summary>
    public static Action<string> Error { get; set; } = message => Console.Error.WriteLine($"RmlUi overlay: {message}");

    /// <summary>Whether the overlay draws. While false, nothing of RmlUi runs at all.</summary>
    public bool Visible { get; set; }

    /// <summary>Whether a fault inside RmlUi has disabled the overlay for good.</summary>
    public bool IsFaulted => NativeOverlay.IsFaulted();

    /// <summary>
    /// Updates the context and draws it into the swap chain's current back buffer, with the game's device state
    /// saved and restored around it. Call on the thread that presents, just before it presents.
    /// </summary>
    /// <param name="swapChain">The game's <c>IDXGISwapChain*</c>.</param>
    public void RenderFrame(nint swapChain)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!Visible)
        {
            return;
        }
        if (!NativeOverlay.RenderD3D11(Context.Handle, swapChain))
        {
            string error = NativeOverlay.Error;
            if (error.Length > 0 && error != lastError)
            {
                lastError = error;
                Error(error);
            }
        }
    }

    /// <summary>Lets go of the back buffer. Call before the swap chain's <c>ResizeBuffers</c>; the next frame takes the new one.</summary>
    public void ReleaseTargets() => NativeOverlay.ReleaseTargets();

    /// <summary>Releases the renderer and system interface; RmlUi is already shut down by the time this runs.</summary>
    protected override void ShutdownHost()
    {
        if (overlayUp)
        {
            NativeOverlay.Shutdown();
            overlayUp = false;
        }
    }
}

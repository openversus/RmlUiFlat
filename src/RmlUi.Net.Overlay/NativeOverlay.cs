using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RmlUi.Overlay;

/// <summary>native/overlay/rmlui_c_overlay.h: RmlUi's DX11 renderer on a game's device and swap chain.</summary>
internal static unsafe partial class NativeOverlay
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_InitializeD3D11")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool InitializeD3D11(nint device, nint window);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_Shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Shutdown();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_GetSystemInterface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint GetSystemInterface();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_GetRenderInterface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint GetRenderInterface();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_RenderD3D11")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool RenderD3D11(nint context, nint swapChain);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_ReleaseTargets")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReleaseTargets();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_ProcessMessage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ProcessMessage(nint context, uint message, nuint wParam, nint lParam, int keyModifierState);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_GetKeyModifierState")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetKeyModifierState();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_IsFaulted")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool IsFaulted();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlOverlay_GetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* GetErrorNative();

    /// <summary>The overlay's last error, or an empty string.</summary>
    public static string Error => Utf8StringMarshaller.ConvertToManaged(GetErrorNative()) ?? "";

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint window, out Rect rect);
}

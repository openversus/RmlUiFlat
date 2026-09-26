using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RmlUi.Desktop;

/// <summary>native/backend/rmlui_c_backend.h: RmlUi's sample desktop backend (window, input, renderer).</summary>
internal static unsafe partial class NativeBackend
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_Initialize", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Initialize(string windowName, int width, int height, [MarshalAs(UnmanagedType.U1)] bool allowResize, RmlRenderer renderer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_GetRenderer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial RmlRenderer GetRenderer();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_GetPixelSize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetPixelSize(int* width, int* height);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_GetDisplayScale")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float GetDisplayScale();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_Shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Shutdown();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_GetSystemInterface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint GetSystemInterface();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_GetRenderInterface")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint GetRenderInterface();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_ProcessEvents")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ProcessEvents(nint context, [MarshalAs(UnmanagedType.U1)] bool powerSave);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_RequestExit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void RequestExit();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_BeginFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void BeginFrame();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_EndFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EndFrame();

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlBackend_PresentFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void PresentFrame();
}

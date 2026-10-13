using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RmlUi;

// Hand-written, paired with native/rmlui_c_manual.cpp: an Rml::FileInterface that asks a C# function for each file.
public readonly unsafe partial struct FileInterface
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public void* UserData;
        public delegate* unmanaged[Cdecl]<void*, byte*, nint, int> Load;
    }

    /// <summary>
    /// A file interface that calls <paramref name="load"/> with each path RmlUi opens (documents, style sheets,
    /// templates, and fonts and images loaded by path) and serves the bytes it returns; null means there is no such
    /// file. Paths arrive as RmlUi resolved them against the referencing document. Install it with
    /// <see cref="Rml.SetFileInterface"/> before <see cref="Rml.Initialise"/> and call <see cref="Destroy"/> after
    /// <see cref="Rml.Shutdown"/>; until then the delegate is kept alive. An exception from <paramref name="load"/>
    /// cannot unwind into RmlUi: it is reported through <see cref="EventListener.UnhandledException"/> and the file
    /// counts as missing. A host does all of this for you: see <c>RmlHostBuilder.Assets</c>.
    /// </summary>
    public static FileInterface Create(Func<string, byte[]?> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        var handle = GCHandle.Alloc(load);
        var callbacks = new Callbacks
        {
            UserData = (void*)GCHandle.ToIntPtr(handle),
            Load = &LoadThunk,
        };
        nint native = Native.Create(&callbacks);
        if (native == 0)
        {
            handle.Free();
            throw new InvalidOperationException("RmlFileInterface_Create failed");
        }
        return new FileInterface(native);
    }

    /// <summary>Destroys a file interface made by <see cref="Create"/> and releases its delegate.</summary>
    public void Destroy()
    {
        if (Handle == 0)
        {
            return;
        }
        GCHandle.FromIntPtr((nint)Native.GetUserData(Handle)).Free();
        Native.Destroy(Handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LoadThunk(void* userData, byte* path, nint buffer)
    {
        string name = Marshal.PtrToStringUTF8((nint)path) ?? "";
        try
        {
            byte[]? data = ((Func<string, byte[]?>)GCHandle.FromIntPtr((nint)userData).Target!)(name);
            if (data == null)
            {
                return 0;
            }
            fixed (byte* bytes = data)
            {
                Native.AssignBuffer(buffer, bytes, (nuint)data.Length);
            }
            return 1;
        }
        catch (Exception e)
        {
            EventListener.UnhandledException(new InvalidOperationException($"Reading \"{name}\" for RmlUi threw.", e));
            return 0;
        }
    }

    private static partial class Native
    {
        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlFileInterface_Create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial nint Create(Callbacks* callbacks);

        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlFileInterface_GetUserData")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void* GetUserData(nint fileInterface);

        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlFileInterface_Destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void Destroy(nint fileInterface);

        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlFileBuffer_Assign")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void AssignBuffer(nint buffer, byte* data, nuint size);
    }
}

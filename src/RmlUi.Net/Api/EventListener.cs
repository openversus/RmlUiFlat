using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RmlUi;

// Hand-written, paired with native/rmlui_c_manual.cpp: an Rml::EventListener whose ProcessEvent calls a C# delegate.
public readonly unsafe partial struct EventListener
{
    /// <summary>
    /// Called with any exception a handler throws. An exception cannot unwind into RmlUi, so it is caught at the
    /// boundary and reported here; by default it is written to stderr.
    /// </summary>
    public static Action<Exception> UnhandledException { get; set; } = e => Console.Error.WriteLine($"RmlUi event handler threw: {e}");

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public void* UserData;
        public delegate* unmanaged[Cdecl]<void*, nint, void> ProcessEvent;
        public delegate* unmanaged[Cdecl]<void*, nint, void> OnAttach;
        public delegate* unmanaged[Cdecl]<void*, nint, void> OnDetach;
    }

    /// <summary>
    /// A listener that calls <paramref name="processEvent"/> for each event. Attach it with
    /// <see cref="Element.AddEventListener(string, EventListener, bool)"/>. Call <see cref="Destroy"/> once it is removed
    /// from every element (or the elements are gone); until then the delegate is kept alive.
    /// </summary>
    public static EventListener Create(Action<Event> processEvent)
    {
        ArgumentNullException.ThrowIfNull(processEvent);
        var handle = GCHandle.Alloc(processEvent);
        var callbacks = new Callbacks
        {
            UserData = (void*)GCHandle.ToIntPtr(handle),
            ProcessEvent = &ProcessEventThunk,
        };
        nint native = Native.Create(&callbacks);
        if (native == 0)
        {
            handle.Free();
            throw new InvalidOperationException("RmlEventListener_Create failed");
        }
        return new EventListener(native);
    }

    /// <summary>Destroys a listener made by <see cref="Create"/> and releases its delegate.</summary>
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
    private static void ProcessEventThunk(void* userData, nint evt)
    {
        try
        {
            ((Action<Event>)GCHandle.FromIntPtr((nint)userData).Target!)(new Event(evt));
        }
        catch (Exception e)
        {
            UnhandledException(e);
        }
    }

    private static partial class Native
    {
        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlEventListener_Create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial nint Create(Callbacks* callbacks);

        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlEventListener_GetUserData")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void* GetUserData(nint listener);

        [LibraryImport(NativeMethods.LibraryName, EntryPoint = "RmlEventListener_Destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial void Destroy(nint listener);
    }
}

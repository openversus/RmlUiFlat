using System;
using System.Collections.Generic;

namespace RmlUi;

// Hand-written: the generated API returns and takes this wherever RmlUi returns or takes an ElementPtr.

/// <summary>
/// An element no document owns yet (RmlUi's <c>ElementPtr</c>): made by <see cref="ElementDocument.CreateElement"/>,
/// <see cref="ElementDocument.CreateTextNode"/> or <see cref="Element.Clone"/>, or taken out of a document by
/// <see cref="Element.RemoveChild"/> or <see cref="Element.ReplaceChild"/>.
/// </summary>
/// <remarks>
/// <para>
/// Attaching it (<see cref="Element.AppendChild"/>, <see cref="Element.InsertBefore"/>, <see cref="Element.ReplaceChild"/>,
/// <see cref="ElementFormControlSelect.Add(OwnedElement, int)"/>, <see cref="ElementTabSet.SetTab(int, OwnedElement)"/>,
/// <see cref="ElementTabSet.SetPanel(int, OwnedElement)"/>)
/// hands it to RmlUi, which frees it with its parent. Attaching it twice throws.
/// </para>
/// <para>
/// One that is never attached is released by <see cref="Dispose"/>. Any still unattached when the host shuts down is
/// released then, before RmlUi shuts down (RmlUi cannot release an element after that), and reported through
/// <see cref="Leaked"/>. There is no finalizer: RmlUi may only be called from its own thread, and not after shutdown.
/// </para>
/// </remarks>
public sealed class OwnedElement : IDisposable
{
    // RmlUi is single-threaded, but a finished OwnedElement may be disposed wherever its owner is; the lock keeps the
    // set consistent without making that an error.
    private static readonly HashSet<OwnedElement> Unattached = [];

    private readonly nint handle;
    private State state;

    private enum State { Owned, Attached, Released }

    private OwnedElement(nint handle) => this.handle = handle;

    /// <summary>
    /// Called with the address of each element (<c>div#row.selected</c>) the host released because nothing attached or
    /// disposed it. By default it is written to stderr.
    /// </summary>
    public static Action<string> Leaked { get; set; } =
        address => Console.Error.WriteLine($"RmlUi element {address} was never attached or disposed; released at shutdown.");

    /// <summary>The element, to set it up before attaching it. Still valid after attaching, as any other handle is.</summary>
    /// <exception cref="ObjectDisposedException">It was released without being attached.</exception>
    public Element Element
    {
        get
        {
            ObjectDisposedException.ThrowIf(state == State.Released, this);
            return new Element(handle);
        }
    }

    /// <summary>Whether RmlUi owns it now, through the element it was attached to.</summary>
    public bool IsAttached => state == State.Attached;

    /// <summary>The element, as <see cref="Element"/> returns it: pass an OwnedElement wherever an Element goes.</summary>
    /// <exception cref="ObjectDisposedException">It was released without being attached.</exception>
    public static implicit operator Element(OwnedElement owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        return owned.Element;
    }

    /// <summary>Releases the element if it was never attached; does nothing once RmlUi owns it.</summary>
    public void Dispose()
    {
        lock (Unattached)
        {
            if (state != State.Owned)
            {
                return;
            }
            state = State.Released;
            Unattached.Remove(this);
        }
        NativeMethods.RmlElement_ReleaseOwned(handle);
    }

    /// <summary>The element's address (<c>div#row.selected</c>), or <c>OwnedElement(released)</c>.</summary>
    public override string ToString() => state == State.Released ? "OwnedElement(released)" : $"OwnedElement({Element.GetAddress(false, false)})";

    /// <summary>Wraps an ElementPtr RmlUi returned; null for a null pointer.</summary>
    internal static OwnedElement? Adopt(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }
        var owned = new OwnedElement(handle);
        lock (Unattached)
        {
            Unattached.Add(owned);
        }
        return owned;
    }

    /// <summary>Hands the element to RmlUi for a call that takes an ElementPtr. RmlUi owns it from then on.</summary>
    /// <exception cref="ArgumentNullException">It is null: the call that should have made it failed.</exception>
    /// <exception cref="InvalidOperationException">It was already attached or released.</exception>
    internal static nint Take(OwnedElement? owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        lock (Unattached)
        {
            if (owned.state != State.Owned)
            {
                throw new InvalidOperationException(owned.state == State.Attached
                    ? "This element was already attached; use the Element the first call returned."
                    : "This element was released (disposed) and cannot be attached.");
            }
            owned.state = State.Attached;
            Unattached.Remove(owned);
        }
        return owned.handle;
    }

    /// <summary>Releases every element nothing attached or disposed, reporting each. The host calls this before Rml::Shutdown.</summary>
    internal static void ReleaseLeftovers()
    {
        OwnedElement[] leftovers;
        lock (Unattached)
        {
            leftovers = [.. Unattached];
        }
        foreach (var owned in leftovers)
        {
            string address = owned.Element.GetAddress(false, false);
            owned.Dispose();
            try
            {
                Leaked(address);
            }
            catch (Exception e)
            {
                // This runs during host teardown; a failing report must not stop RmlUi from shutting down.
                Console.Error.WriteLine($"OwnedElement.Leaked threw: {e}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RmlUi;

// Hand-written, paired with the RmlVariant and RmlDictionary helpers the generator writes into the shim.

/// <summary>
/// An RmlUi value (<c>Rml::Variant</c>), copied into C#: event parameters, attribute values. Make one from a string,
/// number, bool, <see cref="Vector2f"/> or <see cref="Colourb"/> (the conversions are implicit, so
/// <c>element.SetAttribute("max", 10)</c> works); read one with the <c>To</c> methods, which convert between kinds.
/// </summary>
/// <remarks>
/// The shim hands over the value exactly as RmlUi stored it and the conversions happen here, so they never reach
/// RmlUi's converter table, which has gaps. They follow RmlUi's rules where it has them: a bool reads as "1" or "0",
/// and "1", "true", "0" and "false" read as bools. Numbers read and print with the invariant culture.
/// </remarks>
public readonly unsafe partial struct Variant : IEquatable<Variant>
{
    private readonly long integer;
    private readonly double real;
    private readonly string? text;
    private readonly float x, y, z, w;

    private Variant(Type kind, long integer = 0, double real = 0, string? text = null, float x = 0, float y = 0, float z = 0, float w = 0)
    {
        Kind = kind;
        this.integer = integer;
        this.real = real;
        this.text = text;
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    /// <summary>What RmlUi stored. Pointer and list kinds (transforms, decorators, ...) carry no value here.</summary>
    public Type Kind { get; }

    private bool IsInteger => Kind is Type.BOOL or Type.BYTE or Type.CHAR or Type.INT or Type.INT64 or Type.UINT or Type.UINT64;

    private bool IsReal => Kind is Type.FLOAT or Type.DOUBLE;

    /// <summary>A STRING. Null becomes an empty string.</summary>
    public static implicit operator Variant(string value) => new(Type.STRING, text: value ?? "");

    /// <summary>A BOOL.</summary>
    public static implicit operator Variant(bool value) => new(Type.BOOL, integer: value ? 1 : 0);

    /// <summary>An INT.</summary>
    public static implicit operator Variant(int value) => new(Type.INT, integer: value);

    /// <summary>An INT64.</summary>
    public static implicit operator Variant(long value) => new(Type.INT64, integer: value);

    /// <summary>A FLOAT.</summary>
    public static implicit operator Variant(float value) => new(Type.FLOAT, real: value);

    /// <summary>A DOUBLE.</summary>
    public static implicit operator Variant(double value) => new(Type.DOUBLE, real: value);

    /// <summary>A VECTOR2.</summary>
    public static implicit operator Variant(Vector2f value) => new(Type.VECTOR2, x: value.X, y: value.Y);

    /// <summary>A COLOURB.</summary>
    public static implicit operator Variant(Colourb value) => new(Type.COLOURB, x: value.Red, y: value.Green, z: value.Blue, w: value.Alpha);

    /// <summary>Same kind and same value; no conversion (INT 1 is not DOUBLE 1).</summary>
    public static bool operator ==(Variant a, Variant b) => a.Equals(b);

    /// <summary>A different kind or value.</summary>
    public static bool operator !=(Variant a, Variant b) => !a.Equals(b);

    /// <summary>Numbers are nonzero; strings are "1" or "true" (any case), or a nonzero number.</summary>
    public bool ToBool()
    {
        if (Kind == Type.STRING)
        {
            string value = text!.Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && ToDouble() != 0);
        }
        return ToDouble() != 0;
    }

    /// <summary>The value as an int: integers as stored, reals truncated, strings parsed; 0 when there is no number.</summary>
    public int ToInt() => unchecked((int)ToLong());

    /// <summary>The value as a long: integers as stored, reals truncated, strings parsed; 0 when there is no number.</summary>
    public long ToLong() => Kind switch
    {
        _ when IsInteger => integer,
        _ when IsReal => (long)real,
        Type.STRING => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : (long)ParseReal(text!),
        _ => 0,
    };

    /// <summary>The value as a float: see <see cref="ToDouble"/>.</summary>
    public float ToFloat() => (float)ToDouble();

    /// <summary>The value as a double: numbers as stored (UINT64 unsigned), strings parsed; 0 when there is no number.</summary>
    public double ToDouble() => Kind switch
    {
        Type.UINT64 => unchecked((ulong)integer),
        _ when IsInteger => integer,
        _ when IsReal => real,
        Type.STRING => ParseReal(text!),
        _ => 0,
    };

    /// <summary>VECTOR2, VECTOR3, VECTOR4 and COLOURF as their first two components; otherwise zero.</summary>
    public Vector2f ToVector2f() => Kind is Type.VECTOR2 or Type.VECTOR3 or Type.VECTOR4 or Type.COLOURF ? new Vector2f { X = x, Y = y } : default;

    /// <summary>COLOURB as stored; otherwise transparent black.</summary>
    public Colourb ToColourb() => Kind == Type.COLOURB ? new Colourb((byte)x, (byte)y, (byte)z, (byte)w) : default;

    /// <summary>The value as text: strings as stored, bools as "1"/"0" (as RmlUi writes them), numbers and vectors invariant.</summary>
    public override string ToString() => Kind switch
    {
        Type.STRING => text!,
        Type.UINT64 => unchecked((ulong)integer).ToString(CultureInfo.InvariantCulture),
        _ when IsInteger => integer.ToString(CultureInfo.InvariantCulture),
        _ when IsReal => real.ToString(CultureInfo.InvariantCulture),
        Type.VECTOR2 => Join(x, y),
        Type.VECTOR3 => Join(x, y, z),
        Type.VECTOR4 or Type.COLOURF or Type.COLOURB => Join(x, y, z, w),
        Type.NONE or 0 => "",
        _ => $"<{Kind}>",
    };

    /// <summary>Same kind and same value; no conversion (INT 1 is not DOUBLE 1).</summary>
    public bool Equals(Variant other) =>
        Kind == other.Kind && integer == other.integer && real.Equals(other.real) && text == other.text
        && x.Equals(other.x) && y.Equals(other.y) && z.Equals(other.z) && w.Equals(other.w);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Variant other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, integer, real, text, x, y, z, w);

    private static string Join(params float[] values) => string.Join(", ", Array.ConvertAll(values, v => v.ToString(CultureInfo.InvariantCulture)));

    private static double ParseReal(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    /// <summary>A copy of the shim's RmlVariant; null for type 0, which is a null Variant*.</summary>
    internal static Variant? FromNative(RmlVariantNative native)
    {
        if (native.Type == 0)
        {
            return null;
        }
        var kind = (Type)native.Type;
        string? text = kind == Type.STRING ? Utf8StringMarshaller.ConvertToManaged(native.String) ?? "" : null;
        return new Variant(kind, native.Integer, native.Real, text, native.Vector[0], native.Vector[1], native.Vector[2], native.Vector[3]);
    }

    /// <summary>For a call; release with <see cref="FreeNative(RmlVariantNative)"/>.</summary>
    internal RmlVariantNative ToNative()
    {
        var native = new RmlVariantNative { Type = Kind == 0 ? (int)Type.NONE : (int)Kind, Integer = integer, Real = real };
        native.Vector[0] = x;
        native.Vector[1] = y;
        native.Vector[2] = z;
        native.Vector[3] = w;
        native.String = Kind == Type.STRING ? Utf8StringMarshaller.ConvertToUnmanaged(text) : null;
        return native;
    }

    internal static void FreeNative(RmlVariantNative native) => Utf8StringMarshaller.Free(native.String);

    /// <summary>A copy of the shim's RmlDictionary.</summary>
    internal static IReadOnlyDictionary<string, Variant> FromNative(RmlDictionaryNative native)
    {
        var result = new Dictionary<string, Variant>((int)native.Count);
        for (nuint i = 0; i < native.Count; i++)
        {
            string key = Utf8StringMarshaller.ConvertToManaged(native.Data[i].Key) ?? "";
            result[key] = FromNative(native.Data[i].Value) ?? new Variant(Type.NONE);
        }
        return result;
    }

    /// <summary>For a call; release with <see cref="FreeNative(RmlDictionaryNative)"/>.</summary>
    internal static RmlDictionaryNative ToNative(IEnumerable<KeyValuePair<string, Variant>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        KeyValuePair<string, Variant>[] entries = [.. values];
        var data = (RmlDictionaryEntryNative*)NativeMemory.AllocZeroed((nuint)entries.Length, (nuint)sizeof(RmlDictionaryEntryNative));
        for (int i = 0; i < entries.Length; i++)
        {
            data[i].Key = Utf8StringMarshaller.ConvertToUnmanaged(entries[i].Key);
            data[i].Value = entries[i].Value.ToNative();
        }
        return new RmlDictionaryNative(data, (nuint)entries.Length);
    }

    internal static void FreeNative(RmlDictionaryNative native)
    {
        for (nuint i = 0; i < native.Count; i++)
        {
            Utf8StringMarshaller.Free(native.Data[i].Key);
            FreeNative(native.Data[i].Value);
        }
        NativeMemory.Free(native.Data);
    }
}

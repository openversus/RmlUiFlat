using System;
using System.Collections.Generic;
using System.Reflection;

namespace RmlUi.Hosting;

/// <summary>The sources behind <see cref="RmlHostBuilder{TSelf}.Assets(Func{string, byte[]})"/> and the paths they are asked for.</summary>
internal static class AssetSources
{
    /// <summary>
    /// <paramref name="path"/> with <c>/</c> separators, <c>.</c> and <c>..</c> collapsed and no leading <c>/</c>; null
    /// when nothing is left or a <c>..</c> climbs above the root.
    /// </summary>
    internal static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>The embedded resources of <paramref name="assembly"/>, by their logical names read as paths.</summary>
    internal static Func<string, byte[]?> FromAssembly(Assembly assembly)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in assembly.GetManifestResourceNames())
        {
            string? path = Normalize(name);
            if (path != null)
            {
                names.TryAdd(path, name);
            }
        }
        return path =>
        {
            if (!names.TryGetValue(path, out string? name))
            {
                return null;
            }
            using var stream = assembly.GetManifestResourceStream(name)!;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        };
    }

    /// <summary>One loader for RmlUi: normalises the path, then asks each source in order until one has the file.</summary>
    internal static Func<string, byte[]?> Combine(IReadOnlyList<Func<string, byte[]?>> sources) => path =>
    {
        string? normalized = Normalize(path);
        if (normalized == null)
        {
            return null;
        }
        foreach (var source in sources)
        {
            byte[]? data = source(normalized);
            if (data != null)
            {
                return data;
            }
        }
        return null;
    };
}

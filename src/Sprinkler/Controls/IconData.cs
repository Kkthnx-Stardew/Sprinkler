using Avalonia.Media;

namespace Sprinkler.Controls;

/// <summary>
/// Simple line icons on a 24 by 24 grid, drawn with a 2 unit round stroke. The shapes are the open Feather icon set (MIT),
/// stored as SVG path text so they scale to any size and take the colour of the text around them.
/// </summary>
internal static class IconData
{
    private static readonly Dictionary<string, string[]> Paths = new(StringComparer.Ordinal)
    {
        ["package"] = new[] { "M16.5 9.4 7.5 4.2", "M21 16V8a2 2 0 0 0-1-1.7l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.7l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z", "M3.3 7 12 12l8.7-5", "M12 22V12" },
        ["check"] = new[] { "M22 11.1V12a10 10 0 1 1-5.9-9.1", "M22 4 12 14l-3-3" },
        ["arrowup"] = new[] { "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z", "M16 12l-4-4-4 4", "M12 16V8" },
        ["download"] = new[] { "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "M7 10l5 5 5-5", "M12 15V3" },
        ["pointer"] = new[] { "M9 9l5 12 1.8-5.2L21 14 9 9z", "M16 16l4.2 4.2", "M7.2 2.2l.8 2.9", "M5.1 8l-2.9-.8", "M14 4l-2.1 2.1", "M5 14l2.1-2.1" },
        ["sliders"] = new[] { "M4 21v-7", "M4 10V3", "M12 21v-9", "M12 8V3", "M20 21v-5", "M20 12V3", "M1 14h6", "M9 8h6", "M17 16h6" },
        ["book"] = new[] { "M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z", "M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z" },
        ["key"] = new[] { "M8 20a5 5 0 1 0 0-10 5 5 0 0 0 0 10z", "M11.5 11.5 21 2", "M17 6l3 3", "M14 9l2 2" },
        ["alert"] = new[] { "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z", "M12 9v4", "M12 17h.01" },
        ["xcircle"] = new[] { "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z", "M15 9l-6 6", "M9 9l6 6" },
        ["gift"] = new[] { "M20 12v10H4V12", "M2 7h20v5H2z", "M12 22V7", "M12 7H7.5a2.5 2.5 0 0 1 0-5C11 2 12 7 12 7z", "M12 7h4.5a2.5 2.5 0 0 0 0-5C13 2 12 7 12 7z" },
        ["play"] = new[] { "M6 4l14 8-14 8V4z" },
        ["folder"] = new[] { "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" },
        ["clock"] = new[] { "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z", "M12 6v6l4 2" },
        ["help"] = new[] { "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z", "M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3", "M12 17h.01" },
        ["sun"] = new[] { "M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z", "M12 1v2", "M12 21v2", "M4.2 4.2l1.4 1.4", "M18.4 18.4l1.4 1.4", "M1 12h2", "M21 12h2", "M4.2 19.8l1.4-1.4", "M18.4 5.6l1.4-1.4" },
        ["moon"] = new[] { "M21 12.8A9 9 0 1 1 11.2 3 7 7 0 0 0 21 12.8z" },
        ["power"] = new[] { "M18.4 6.6a9 9 0 1 1-12.8 0", "M12 2v10" },
        ["skip"] = new[] { "M5 4l10 8-10 8V4z", "M19 5v14" },
        ["refresh"] = new[] { "M1 4v6h6", "M3.5 15a9 9 0 1 0 2.1-9.4L1 10" },
        ["shield"] = new[] { "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z", "M9 12l2 2 4-4" },
        ["upload"] = new[] { "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "M17 8l-5-5-5 5", "M12 3v12" },
        ["more"] = new[] { "M12 5h.01", "M12 12h.01", "M12 19h.01" },
        ["close"] = new[] { "M18 6 6 18", "M6 6l12 12" },
        ["search"] = new[] { "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z", "M21 21l-4.3-4.3" },
        ["external"] = new[] { "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6", "M15 3h6v6", "M10 14 21 3" },
        ["chevron"] = new[] { "M6 9l6 6 6-6" },
    };

    /// <summary>Icons that are drawn as solid shapes instead of outlines.</summary>
    private static readonly HashSet<string> Filled = new(StringComparer.Ordinal) { "play" };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public static bool IsFilled(string kind) => Filled.Contains(kind);

    public static Geometry? Get(string? kind)
    {
        if (string.IsNullOrEmpty(kind))
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var cached))
                return cached;
            if (!Paths.TryGetValue(kind, out var parts))
                return null;
            var geometry = Geometry.Parse(string.Join(' ', parts));
            Cache[kind] = geometry;
            return geometry;
        }
    }

    public static IEnumerable<string> Names => Paths.Keys;
}

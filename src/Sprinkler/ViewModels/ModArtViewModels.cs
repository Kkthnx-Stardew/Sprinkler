using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sprinkler.ViewModels;

/// <summary>What the small mod picture needs: the downloaded image when there is one, otherwise a coloured initial.</summary>
internal interface IModArt
{
    Bitmap? Icon { get; }

    bool HasIcon { get; }

    bool NoIcon { get; }

    string Initial { get; }

    IBrush AvatarBrush { get; }
}

/// <summary>Decodes pictures once and keeps them small: icons are shown at most 48 pixels wide, so they are decoded at twice that.</summary>
internal static class ArtImages
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public static Bitmap? Get(string? path)
    {
        if (path is null)
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
            Bitmap? bitmap = null;
            try
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 96, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Core.Log.Write($"Could not read the picture {path}: {ex.Message}");
            }
            Cache[path] = bitmap;
            return bitmap;
        }
    }
}

/// <summary>A steady colour and letter for a mod that has no picture, so the list still reads as a list of things.</summary>
internal static class Avatar
{
    // all dark enough for white text to pass the 4.5 : 1 contrast rule
    private static readonly string[] Colors = { "#9A5B13", "#3B6A2A", "#1B5C9E", "#8A3B6B", "#A33527", "#4F5D75", "#6A4C93" };

    public static string Initial(string name)
    {
        var letter = name.FirstOrDefault(char.IsLetterOrDigit);
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }

    public static IBrush Brush(string name)
    {
        var hash = 17;
        foreach (var c in name)
            hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        return new SolidColorBrush(Color.Parse(Colors[(hash & 0x7FFFFFFF) % Colors.Length]));
    }
}

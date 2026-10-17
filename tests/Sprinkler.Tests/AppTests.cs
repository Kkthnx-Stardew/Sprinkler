using System.Xml.Linq;
using Sprinkler.Controls;
using Sprinkler.ViewModels;
using Xunit;

namespace Sprinkler.Tests;

public class ContrastTests
{
    private static string PalettePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Sprinkler.sln")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new FileNotFoundException("Sprinkler.sln"), "src", "Sprinkler", "Theme", "Palette.axaml");
    }

    /// <summary>Reads the colours of one theme straight out of the palette file the app uses.</summary>
    private static Dictionary<string, (int R, int G, int B)> Theme(string variant)
    {
        XNamespace av = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(PalettePath());
        var theme = doc.Descendants(av + "ResourceDictionary").First(d => (string?)d.Attribute(x + "Key") == variant);
        return theme.Elements(av + "SolidColorBrush").ToDictionary(
            e => ((string)e.Attribute(x + "Key")!).Replace("Brush", ""),
            e => Rgb((string)e.Attribute("Color")!));
    }

    private static (int R, int G, int B) Rgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8)
            hex = hex[2..]; // drop alpha
        return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16));
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Ratio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Every text colour next to the background it is drawn on.</summary>
    public static IEnumerable<object[]> Pairs()
    {
        var pairs = new (string Text, string Background)[]
        {
            ("Ink", "Bg"), ("Ink", "Panel"), ("Ink", "Panel2"), ("Ink", "AccentSoft"),
            ("Muted", "Bg"), ("Muted", "Panel"), ("Muted", "Panel2"), ("Muted", "AccentSoft"),
            ("AccentText", "Panel"), ("AccentText", "AccentSoft"), ("AccentText", "Bg"),
            ("AccentInk", "Accent"), ("AccentInk", "AccentHover"),
            ("Good", "GoodSoft"), ("Good", "Panel"), ("Good", "Bg"),
            ("Info", "InfoSoft"), ("Info", "Panel"), ("Info", "Bg"),
            ("Warn", "WarnSoft"), ("Warn", "Panel"), ("Warn", "Bg"),
            ("Bad", "BadSoft"), ("Bad", "Panel"), ("Bad", "Bg"),
        };
        foreach (var variant in new[] { "Light", "Dark" })
            foreach (var (text, background) in pairs)
                yield return new object[] { variant, text, background };
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TextIsReadableOnItsBackground(string variant, string text, string background)
    {
        var theme = Theme(variant);
        var ratio = Ratio(theme[text], theme[background]);
        Assert.True(ratio >= 4.5, $"{variant}: {text} on {background} is {ratio:0.00} : 1, below the 4.5 : 1 AA minimum.");
    }

    [Fact]
    public void BothThemesDefineTheSameColours()
    {
        Assert.Equal(Theme("Light").Keys.OrderBy(k => k), Theme("Dark").Keys.OrderBy(k => k));
    }

    [Fact]
    public void WhiteTextIsReadableOnEveryAvatarAndSourceBadgeColour()
    {
        var white = (255, 255, 255);
        var colors = new[] { "#9A5B13", "#3B6A2A", "#1B5C9E", "#8A3B6B", "#A33527", "#4F5D75", "#6A4C93", "#B0520D", "#C24A22", "#3A3F4B", "#2E6BA8" };
        foreach (var c in colors)
            Assert.True(Ratio(white, Rgb(c)) >= 4.5, $"White on {c} is {Ratio(white, Rgb(c)):0.00} : 1.");
    }
}

public class AppLogicTests
{
    [Fact]
    public void AvatarsAreSteadyAndNeverEmpty()
    {
        Assert.Equal("C", Avatar.Initial("Content Patcher"));
        Assert.Equal("E", Avatar.Initial("  [East] Scarp"));
        Assert.Equal("?", Avatar.Initial("---"));
        Assert.Equal(Avatar.Brush("x.y").ToString(), Avatar.Brush("X.Y").ToString());
    }

    [Fact]
    public void EveryIconNamedInTheScreensExists()
    {
        var names = new[] { "package", "check", "arrowup", "download", "pointer", "sliders", "book", "key", "alert", "xcircle", "gift", "play", "folder", "clock", "help", "sun", "moon", "power", "skip", "refresh", "shield", "upload", "more", "close", "sprinkler" };
        foreach (var name in names.Where(n => n != "sprinkler"))
            Assert.True(IconData.Has(name), name);
        Assert.False(IconData.Has("nope"));
    }

    [Fact]
    public void TheCommandLineIsParsedTheWayTheHelpSaysAndIgnoresJunk()
    {
        var o = Options.Parse(new[] { "--game", @"C:\g", "--mods", @"C:\g\Mods", "--check", "nxm://stardewvalley/mods/1/files/2?key=a&expires=3", "--bogus" });
        Assert.Equal(@"C:\g", o.GamePath);
        Assert.Equal(@"C:\g\Mods", o.ModsPath);
        Assert.True(o.Check);
        Assert.StartsWith("nxm://", o.NxmUrl);
        Assert.False(o.Help);

        Assert.True(Options.Parse(new[] { "--install-shortcut" }).InstallShortcut);
        Assert.True(Options.Parse(new[] { "-v" }).Version);
        Assert.Null(Options.Parse(new[] { "--game" }).GamePath); // a flag with no value is ignored, not a crash
    }

    [Fact]
    public void FormattingReadsInPlainLanguage()
    {
        Assert.Equal("1 mod", Fmt.Plural(1, "mod", "mods"));
        Assert.Equal("3 mods", Fmt.Plural(3, "mod", "mods"));
        Assert.Equal("12 MB", Fmt.Bytes(12_400_000));
        Assert.Equal("1 KB", Fmt.Bytes(10));
        Assert.Equal("just now", Fmt.Ago(DateTime.UtcNow));
        Assert.Equal("5 min ago", Fmt.Ago(DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void ListsAreOnlyTouchedWhenTheyChange()
    {
        var list = new System.Collections.ObjectModel.ObservableCollection<string> { "a", "b" };
        var changes = 0;
        list.CollectionChanged += (_, _) => changes++;
        Fmt.Sync(list, new[] { "a", "b" });
        Assert.Equal(0, changes);
        Fmt.Sync(list, new[] { "a", "c" });
        Assert.Equal(new[] { "a", "c" }, list);
    }

    [Fact]
    public void TheTourHasAStoryAndEveryStepSaysSomething()
    {
        Assert.True(Content.Tour.Count >= 5);
        Assert.All(Content.Tour, s => Assert.False(string.IsNullOrWhiteSpace(s.Title + s.Text + s.Icon)));
        Assert.All(Content.Tour.Where(s => s.Icon != "sprinkler"), s => Assert.True(IconData.Has(s.Icon), s.Icon));
        Assert.Equal("Every mod. Current. One click.", Content.Tagline);
    }

    [Theory]
    [InlineData("Linux", "Steam Deck")]
    [InlineData("Mac", "Contents/MacOS")]
    [InlineData("Windows", "ModifiableWindowsApps")]
    public void GameFolderHelpMatchesTheSystem(string os, string mention) =>
        Assert.Contains(mention, Content.FindGameHelp(os));
}

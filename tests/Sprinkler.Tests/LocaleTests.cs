using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Text.RegularExpressions;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>The language files must stay complete and safe to format. These tests read the files that ship inside the program.</summary>
public class LocaleTests
{
    private static readonly Loc Source = new();

    public static IEnumerable<object[]> Languages() => Loc.All.Select(l => new object[] { l.Code });

    private static Language Lang(string code) => Loc.All.Single(l => l.Code == code);

    private static IReadOnlyDictionary<string, string> English => Source.Strings(Loc.English);

    /// <summary>Base names of the counted phrases: those with both a _one and an _other form in English.</summary>
    private static HashSet<string> PluralBases() =>
        English.Keys.Where(k => k.EndsWith("_other", StringComparison.Ordinal) && English.ContainsKey(k[..^"_other".Length] + "_one"))
            .Select(k => k[..^"_other".Length]).ToHashSet();

    private static IEnumerable<string> Categories(PluralRule rule) => rule switch
    {
        PluralRule.OneOther or PluralRule.ZeroOneOther => new[] { "one", "other" },
        PluralRule.EastSlavic or PluralRule.Polish => new[] { "one", "few", "many" },
        _ => new[] { "other" },
    };

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void EnglishIsTheCompleteSource()
    {
        Assert.True(English.Count > 500);
        Assert.All(English, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
        Assert.Contains("n.mod", PluralBases());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageHasEveryKey(string code)
    {
        var strings = Source.Strings(code);
        var language = Lang(code);
        var bases = PluralBases();
        var missing = new List<string>();

        foreach (var key in English.Keys)
        {
            var isPluralForm = bases.Any(b => key == b + "_one" || key == b + "_other");
            if (!isPluralForm && !strings.ContainsKey(key))
                missing.Add(key);
        }
        foreach (var b in bases)
            foreach (var category in Categories(language.Plural))
                if (!strings.ContainsKey(b + "_" + category))
                    missing.Add(b + "_" + category);

        Assert.True(missing.Count == 0, code + " lacks: " + string.Join(", ", missing.Take(12)));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void NoLanguageHasKeysTheAppDoesNotKnow(string code)
    {
        var strings = Source.Strings(code);
        var bases = PluralBases();
        var allowedSuffixes = new[] { "_one", "_few", "_many", "_other" };
        var unknown = strings.Keys.Where(k =>
            !English.ContainsKey(k) && !bases.Any(b => allowedSuffixes.Any(s => k == b + s))).ToList();
        Assert.True(unknown.Count == 0, code + " has unknown keys: " + string.Join(", ", unknown.Take(12)));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void PlaceholdersMatchEnglishAndTextsFormat(string code)
    {
        var strings = Source.Strings(code);
        var bases = PluralBases();
        var problems = new List<string>();

        foreach (var (key, text) in strings)
        {
            var englishKey = key;
            var pluralBase = bases.FirstOrDefault(b => key.StartsWith(b + "_", StringComparison.Ordinal)
                && key[(b.Length + 1)..] is "one" or "few" or "many" or "other");
            if (pluralBase is not null)
                englishKey = pluralBase + "_other";
            if (!English.TryGetValue(englishKey, out var reference))
                continue;

            var wanted = Placeholders(reference);
            var found = Placeholders(text);
            // a singular form may say "one" instead of "1 {0}", but it may never use a value the app does not pass
            var ok = pluralBase is not null ? found.IsSubsetOf(wanted) : found.SetEquals(wanted);
            if (!ok)
                problems.Add(key);

            try
            {
                _ = string.Format(CultureInfo.InvariantCulture, text, "a", "b", "c", "d");
            }
            catch (FormatException)
            {
                problems.Add(key + " (format)");
            }
        }
        Assert.True(problems.Count == 0, code + " has placeholder problems: " + string.Join(", ", problems.Take(12)));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TextsAreTidy(string code)
    {
        foreach (var (key, text) in Source.Strings(code))
        {
            Assert.False(string.IsNullOrWhiteSpace(text) && key != "core.sum_join", code + " " + key + " is empty");
            Assert.DoesNotContain('—', text); // no em dashes anywhere
            Assert.DoesNotContain("TODO", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', text);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void DateFormatsWork(string code)
    {
        var format = Source.Strings(code)["fmt.datetime"];
        var text = new DateTime(2026, 10, 17, 14, 5, 0).ToString(format, CultureInfo.InvariantCulture);
        Assert.Contains("2026", text);
    }

    [Fact]
    public void CountedPhrasesPickTheRightForm()
    {
        var ru = new Loc();
        ru.SetLanguage("ru");
        Assert.Equal("1 мод", ru.Plural("n.mod", 1));
        Assert.Equal("2 мода", ru.Plural("n.mod", 2));
        Assert.Equal("5 модов", ru.Plural("n.mod", 5));
        Assert.Equal("11 модов", ru.Plural("n.mod", 11));
        Assert.Equal("21 мод", ru.Plural("n.mod", 21));
        Assert.Equal("0 модов", ru.Plural("n.mod", 0));

        var pl = new Loc();
        pl.SetLanguage("pl");
        Assert.Equal("1 mod", pl.Plural("n.mod", 1));
        Assert.Equal("3 mody", pl.Plural("n.mod", 3));
        Assert.Equal("12 modów", pl.Plural("n.mod", 12));
        Assert.Equal("22 mody", pl.Plural("n.mod", 22));

        var fr = new Loc();
        fr.SetLanguage("fr");
        Assert.Equal("0 mod", fr.Plural("n.mod", 0));
        Assert.Equal("1 mod", fr.Plural("n.mod", 1));
        Assert.Equal("2 mods", fr.Plural("n.mod", 2));

        var ja = new Loc();
        ja.SetLanguage("ja");
        Assert.Equal("MOD 1 件", ja.Plural("n.mod", 1));
    }

    [Fact]
    public void AMissingTranslationFallsBackToEnglish()
    {
        var loc = new Loc();
        loc.SetLanguage("de");
        Assert.Equal("Abbrechen", loc["ui.cancel"]);
        Assert.Equal("no.such.key", loc["no.such.key"]);

        // a key the language file lacks is shown in English, never as a hole
        var german = loc.Load("de");
        var kept = german["ui.cancel"];
        german.Remove("ui.cancel");
        try
        {
            Assert.Equal("Cancel", loc["ui.cancel"]);
        }
        finally
        {
            german["ui.cancel"] = kept;
        }
    }

    [Theory]
    [InlineData("pt_PT.UTF-8", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("de-AT", "de")]
    [InlineData("en_US", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh_SG.UTF-8", "zh-Hans")]
    [InlineData("ja_JP.UTF-8", "ja")]
    [InlineData("uk", "uk")]
    public void SystemLanguagesAreMatched(string system, string expected) =>
        Assert.Equal(expected, Loc.Match(new[] { system })?.Code);

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh_HK.UTF-8")]
    [InlineData("nb_NO")]
    [InlineData("C")]
    [InlineData("POSIX")]
    [InlineData("xx")]
    public void LanguagesWeDoNotShipAreNotGuessed(string system) =>
        Assert.Null(Loc.Match(new[] { system }));

    [Fact]
    public void ChoosingByCodeIsStrictAndUnknownCodesMeanEnglish()
    {
        Assert.Equal("ko", Loc.Resolve("KO").Code);
        Assert.Equal("en", Loc.Resolve("tlh").Code);
    }
}

public class LanguageUiTests
{
    [AvaloniaFact]
    public async Task ChoosingALanguageRedrawsTheTextAndIsRemembered()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        var vm = new MainViewModel(rig.Service) { Ui = new FakeUi(), SuppressTour = true };
        vm.Refresh();
        try
        {
            Assert.Equal("All", vm.ModChips[0].Text.Split(' ')[0]);
            Assert.Equal(18, vm.LanguageChoices.Count);
            Assert.Equal("auto", vm.LanguageChoices[0].Code);

            vm.SelectedLanguage = vm.LanguageChoices.Single(c => c.Code == "de");
            var end = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < end && !vm.ModChips[0].Text.StartsWith("Alle", StringComparison.Ordinal))
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }

            Assert.StartsWith("Alle", vm.ModChips[0].Text);
            Assert.Equal("de", vm.Snap!.Settings.Language);
            Assert.Equal("de", vm.SelectedLanguage!.Code);
            Assert.Equal("Abbrechen", Loc.T("ui.cancel"));
        }
        finally
        {
            rig.Service.SetLanguage("en");
            Loc.Instance.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
        }
    }
}

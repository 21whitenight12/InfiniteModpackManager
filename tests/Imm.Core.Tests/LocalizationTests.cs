using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Imm.Core.Localization;
using Xunit;

namespace Imm.Core.Tests;

/// <summary>
/// Tests assert user-facing text, so the whole assembly runs with the Russian catalog — that is the
/// reference language of the product. English coverage is checked explicitly below.
/// </summary>
internal static class LocalizationBootstrap
{
    [ModuleInitializer]
    internal static void Init() => Loc.Instance.SetLanguage(Loc.Russian);
}

public sealed class LocalizationTests
{
    private static readonly ResourceManager Resources =
        new("Imm.Core.Localization.Strings", typeof(Loc).Assembly);

    private static HashSet<string> Keys(string culture) =>
        Resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void BothLanguages_CarryTheSameKeys()
    {
        var english = Keys("en");
        var russian = Keys("ru");

        Assert.NotEmpty(english);
        Assert.Equal(english.Count, russian.Count);
        Assert.Empty(english.Except(russian));
        Assert.Empty(russian.Except(english));
    }

    [Fact]
    public void EveryValue_IsFilledInBothLanguages()
    {
        foreach (var culture in new[] { "en", "ru" })
        {
            foreach (var key in Keys(culture))
            {
                Assert.False(string.IsNullOrWhiteSpace(Resources.GetString(key, CultureInfo.GetCultureInfo(culture))),
                    $"{culture}:{key} is empty");
            }
        }
    }

    [Fact]
    public void PacksCount_FollowsThePluralRulesOfTheActiveLanguage()
    {
        Loc.Instance.SetLanguage(Loc.English);
        Assert.Equal("1 pack", Loc.PacksCount(1));
        Assert.Equal("3 packs", Loc.PacksCount(3));
        Assert.Equal("11 packs", Loc.PacksCount(11));

        Loc.Instance.SetLanguage(Loc.Russian);
        Assert.Equal("1 сборка", Loc.PacksCount(1));
        Assert.Equal("3 сборки", Loc.PacksCount(3));
        Assert.Equal("5 сборок", Loc.PacksCount(5));
        Assert.Equal("11 сборок", Loc.PacksCount(11));
        Assert.Equal("21 сборка", Loc.PacksCount(21));
    }

    [Fact]
    public void SwitchingLanguage_RepaintsIndexerBindings()
    {
        var changes = 0;
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == "Item[]")
            {
                changes++;
            }
        }

        Loc.Instance.PropertyChanged += Handler;
        try
        {
            Loc.Instance.SetLanguage(Loc.English);
            var english = Loc.T("Nav_Library");
            Loc.Instance.SetLanguage(Loc.Russian);
            var russian = Loc.T("Nav_Library");

            Assert.Equal("Library", english);
            Assert.Equal("Библиотека", russian);
            Assert.True(changes >= 2, "indexer bindings were not notified");
        }
        finally
        {
            Loc.Instance.PropertyChanged -= Handler;
            Loc.Instance.SetLanguage(Loc.Russian);
        }
    }

    [Fact]
    public void MissingKey_FallsBackToTheKeyItself()
    {
        Assert.Equal("No_Such_Key", Loc.T("No_Such_Key"));
    }
}

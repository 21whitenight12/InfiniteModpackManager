using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Imm.Core.Localization;

/// <summary>
/// Runtime string catalog. <c>Strings.resx</c> is the neutral (English) resource set, <c>Strings.ru.resx</c>
/// the Russian satellite. UI binds through the indexer so a language switch repaints without a restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";
    public const string Russian = "ru";

    private static readonly ResourceManager Resources =
        new("Imm.Core.Localization.Strings", typeof(Loc).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo(English);

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML: <c>{Binding [Nav_Library], Source={x:Static loc:Loc.Instance}}</c>.</summary>
    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public CultureInfo Culture => _culture;

    /// <summary>Two-letter code of the active language ("en" / "ru").</summary>
    public string LanguageCode => _culture.TwoLetterISOLanguageName;

    public void SetLanguage(string? code)
    {
        var normalized = string.Equals(code, Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English;
        if (string.Equals(normalized, LanguageCode, StringComparison.Ordinal))
        {
            return;
        }

        _culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        CultureInfo.CurrentUICulture = _culture;

        // Empty path refreshes every indexer binding at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) =>
        string.Format(Instance._culture, Instance[key], args);

    /// <summary>Russian needs three plural forms, English two — pass one/few/many keys.</summary>
    public static string Plural(string oneKey, string fewKey, string manyKey, int count)
    {
        if (!string.Equals(Instance.LanguageCode, Russian, StringComparison.Ordinal))
        {
            return count == 1 ? F(oneKey, count) : F(fewKey, count);
        }

        var mod100 = Math.Abs(count) % 100;
        var mod10 = Math.Abs(count) % 10;
        if (mod100 is >= 11 and <= 14)
        {
            return F(manyKey, count);
        }

        return mod10 switch
        {
            1 => F(oneKey, count),
            >= 2 and <= 4 => F(fewKey, count),
            _ => F(manyKey, count),
        };
    }

    /// <summary>"3 packs" / "3 сборки" for the library header and the sidebar footer.</summary>
    public static string PacksCount(int count) =>
        $"{count} {Plural("Plural_Packs_One", "Plural_Packs_Few", "Plural_Packs_Many", count)}";

    public static string LinesCount(int count) =>
        $"{count} {Plural("Plural_Lines_One", "Plural_Lines_Few", "Plural_Lines_Many", count)}";
}

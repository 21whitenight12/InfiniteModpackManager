using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Scanning;

namespace Imm.App.ViewModels;

/// <summary>UI wrapper around LibraryEntry + manifest data with pre-formatted display strings.</summary>
public partial class LibraryEntryViewModel : ObservableObject
{
    private readonly LibraryEntry _entry;
    private readonly MainViewModel? _owner;
    private ModpackManifest? _manifest;

    public LibraryEntryViewModel(LibraryEntry entry, ModpackManifest? manifest, MainViewModel? owner = null)
    {
        _entry = entry;
        _manifest = manifest;
        _owner = owner;
    }

    public Guid Id => _entry.Id;

    public string Name => _entry.Name;

    public string RootPath => _entry.RootPath;

    private static readonly string[] CoverFileNames = ["cover.png", "cover.jpg", "cover.jpeg", "cover.webp"];

    private string? _coverPath;
    private bool _coverPathResolved;
    private string? _coverImageKey;
    private ImageSource? _coverImage;

    /// <summary>Cover file in the pack root (fixed lookup order); cached until <see cref="Refresh"/>.</summary>
    public string? CoverPath
    {
        get
        {
            if (!_coverPathResolved)
            {
                _coverPath = FindCoverPath();
                _coverPathResolved = true;
            }

            return _coverPath;
        }
    }

    /// <summary>
    /// Decoded cover. Loaded with <see cref="BitmapCacheOption.OnLoad"/> and frozen on purpose:
    /// WPF must not hold a handle on cover.* — otherwise the next SetCoverAsync cannot replace
    /// the file (a string-bound ImageSource keeps the file open; verified on this machine).
    /// </summary>
    public ImageSource? CoverImage
    {
        get
        {
            var path = CoverPath;
            if (path is null)
            {
                return null;
            }

            var key = CoverKey(path);
            if (_coverImage is not null && string.Equals(_coverImageKey, key, StringComparison.Ordinal))
            {
                return _coverImage;
            }

            _coverImage = LoadFrozen(path);
            _coverImageKey = _coverImage is null ? null : key;
            return _coverImage;
        }
    }

    private string? FindCoverPath()
    {
        foreach (var name in CoverFileNames)
        {
            if (File.Exists(LongPath.Normalize(Path.Combine(_entry.RootPath, name))))
            {
                return Path.Combine(_entry.RootPath, name);
            }
        }

        return null;
    }

    /// <summary>Path + size + write time: a replaced cover with the same name still invalidates the cache.</summary>
    private static string CoverKey(string path)
    {
        var info = new FileInfo(LongPath.Normalize(path));
        return info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path;
    }

    private static ImageSource? LoadFrozen(string path)
    {
        try
        {
            using var stream = File.OpenRead(LongPath.Normalize(path));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            // Битая или недоступная картинка → плейсхолдер карточки, приложение не падает.
            return null;
        }
    }

    /// <summary>UI hook after RenameAsync / SetCoverAsync touch the entry.</summary>
    public void Refresh()
    {
        _coverPathResolved = false;
        _coverImage = null;
        _coverImageKey = null;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Applies a user rename to the wrapped entry: the service updates its own deserialized copy,
    /// so the VM's instance must be synced explicitly before the notification.
    /// </summary>
    public void ApplyName(string name)
    {
        _entry.Name = name;
        OnPropertyChanged(nameof(Name));
    }

    public GameType GameType => _manifest?.GameType ?? GameType.Unknown;

    public LaunchEngine Engine => _manifest?.Engine ?? LaunchEngine.Direct;

    public PackSource Source => _manifest?.Source ?? PackSource.System;

    public string GameDisplayName => GameType switch
    {
        GameType.SkyrimLE => "Skyrim LE",
        GameType.SkyrimSE => "Skyrim SE",
        GameType.Fallout4 => "Fallout 4",
        GameType.FalloutNV => "Fallout NV",
        _ => "Игра не определена",
    };

    public string EngineDisplayName => (Engine, Source) switch
    {
        (LaunchEngine.Mo2RootBuilder, _) => "MO2 + Root Builder",
        (LaunchEngine.Mo2Standard, _) => "MO2 Portable",
        (LaunchEngine.Direct, PackSource.Portable) => "Direct · Портативная игра",
        _ => "Direct",
    };

    public string LastPlayedText => _entry.LastPlayedAtUtc is null
        ? "—"
        : _entry.LastPlayedAtUtc.Value.ToLocalTime().ToString("dd.MM.yyyy");

    /// <summary>Raw sort key (CollectionView SortDescriptions binds this).</summary>
    public DateTime? LastPlayedAtUtc => _entry.LastPlayedAtUtc;

    /// <summary>Raw sort key.</summary>
    public int PlaytimeMinutes => _entry.PlaytimeMinutes;

    public string PlaytimeText
    {
        get
        {
            var minutes = _entry.PlaytimeMinutes;
            return minutes < 60 ? $"{minutes} мин" : $"{minutes / 60} ч {minutes % 60} мин";
        }
    }

    /// <summary>User notes from library.json; null → section hidden.</summary>
    public string? NotesText => string.IsNullOrWhiteSpace(_entry.Notes) ? null : _entry.Notes;

    /// <summary>MO2 profile name from manifest; null → row hidden.</summary>
    public string? ProfileText => string.IsNullOrWhiteSpace(_manifest?.Mo2?.ProfileName)
        ? null
        : _manifest.Mo2.ProfileName;

    /// <summary>Portable pack vs detected system game copy.</summary>
    public string SourceText => Source switch
    {
        PackSource.Portable => "Портативная",
        PackSource.System => "Системная игра",
        _ => "—",
    };

    /// <summary>Sort key by game title.</summary>
    public string GameSortKey => GameDisplayName;

    /// <summary>MO2 present but no profile resolved by the scanner → needs user attention (IMM.md: no silent guesses).</summary>
    public bool HasWarnings =>
        (_manifest?.Mo2 is not null && string.IsNullOrWhiteSpace(_manifest.Mo2.ProfileName))
        || (HasMo2Files() && _manifest?.Mo2 is null);

    [ObservableProperty]
    private bool _isSelected;

    private bool HasMo2Files() =>
        Mo2Locator.ResolveInstance(_entry.RootPath, _manifest?.Mo2?.Directory) is not null;

    // ----- context menu commands (command objects live here so ContextMenu
    // bindings never cross the popup tree boundary; owner executes them) -----

    /// <summary>Profiles of the resolved MO2 instance; empty when no instance or no profiles folder.</summary>
    public IReadOnlyList<string> AvailableProfiles
    {
        get
        {
            var instance = Mo2Locator.ResolveInstance(_entry.RootPath, _manifest?.Mo2?.Directory);
            return instance is null ? [] : Mo2Locator.ListProfiles(instance);
        }
    }

    [RelayCommand]
    private void SetProfile(string profile) => _owner?.SetProfile(this, profile);

    [RelayCommand]
    private void ChooseGameFolder() => _owner?.ChooseGameFolder(this);

    [RelayCommand]
    private void Rename() => _owner?.RenameEntry(this);

    [RelayCommand]
    private void ChangeCover() => _owner?.ChangeCover(this);

    [RelayCommand]
    private void OpenFolder() => _owner?.OpenPackFolder(this);

    [RelayCommand]
    private void Rescan() => _owner?.RescanCommand.Execute(this);

    [RelayCommand]
    private void Delete() => _owner?.RemoveCommand.Execute(this);

    public void UpdateManifest(ModpackManifest manifest)
    {
        _manifest = manifest;
        OnPropertyChanged(string.Empty);
    }
}

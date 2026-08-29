using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.Core.Domain;
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

    /// <summary>Cover image from the pack root; null → placeholder gradient in the card template.</summary>
    public string? CoverPath
    {
        get
        {
            foreach (var name in new[] { "cover.png", "cover.jpg", "cover.jpeg", "cover.webp" })
            {
                var path = Path.Combine(_entry.RootPath, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }

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
        File.Exists(Path.Combine(_entry.RootPath, "ModOrganizer.exe"))
        || File.Exists(Path.Combine(_entry.RootPath, "MO2", "ModOrganizer.exe"));

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

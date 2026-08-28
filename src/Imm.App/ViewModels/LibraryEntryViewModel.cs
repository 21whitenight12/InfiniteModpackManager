using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Imm.Core.Domain;
using Imm.Core.Scanning;

namespace Imm.App.ViewModels;

/// <summary>UI wrapper around LibraryEntry + manifest data with pre-formatted display strings.</summary>
public partial class LibraryEntryViewModel : ObservableObject
{
    private readonly LibraryEntry _entry;
    private ModpackManifest? _manifest;

    public LibraryEntryViewModel(LibraryEntry entry, ModpackManifest? manifest)
    {
        _entry = entry;
        _manifest = manifest;
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

    public string PlaytimeText
    {
        get
        {
            var minutes = _entry.PlaytimeMinutes;
            return minutes < 60 ? $"{minutes} мин" : $"{minutes / 60} ч {minutes % 60} мин";
        }
    }

    /// <summary>MO2 present but no profile resolved by the scanner → needs user attention (IMM.md: no silent guesses).</summary>
    public bool HasWarnings => HasMo2Files() && _manifest?.Mo2 is null;

    [ObservableProperty]
    private bool _isSelected;

    private bool HasMo2Files() =>
        File.Exists(Path.Combine(_entry.RootPath, "ModOrganizer.exe"));

    public void UpdateManifest(ModpackManifest manifest)
    {
        _manifest = manifest;
        OnPropertyChanged(string.Empty);
    }
}

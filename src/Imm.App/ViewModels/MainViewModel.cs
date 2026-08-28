using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Library;
using Imm.Core.Scanning;
using Microsoft.Win32;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Imm.App.ViewModels;

/// <summary>Library page state: modpack cards, selection, import (IMM.md MVP #1).</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly ISnackbarService _snackbar;
    private readonly ILogger _logger;

    public MainViewModel(ILibraryService library, ISnackbarService snackbar, ILogger logger)
    {
        _library = library;
        _snackbar = snackbar;
        _logger = logger.ForContext<MainViewModel>();
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoEntries));
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        ApplySort();
    }

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = [];

    /// <summary>Sorted view fed to the ItemsControl (Entries is the source of truth).</summary>
    public ICollectionView EntriesView { get; }

    public IReadOnlyList<string> SortOptions { get; } = ["По названию", "По последнему запуску", "По времени игры"];

    [ObservableProperty]
    private string _selectedSortOption = "По названию";

    [ObservableProperty]
    private bool _isGridView = true;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        EntriesView.Filter = string.IsNullOrWhiteSpace(SearchText)
            ? null
            : o => o is LibraryEntryViewModel e &&
                   (e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    e.GameDisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void SetGridView() => IsGridView = true;

    [RelayCommand]
    private void SetListView() => IsGridView = false;

    partial void OnSelectedSortOptionChanged(string value) => ApplySort();

    private void ApplySort()
    {
        EntriesView.SortDescriptions.Clear();
        SortDescription description = SelectedSortOption switch
        {
            "По последнему запуску" => new(nameof(LibraryEntryViewModel.LastPlayedAtUtc), ListSortDirection.Descending),
            "По времени игры" => new(nameof(LibraryEntryViewModel.PlaytimeMinutes), ListSortDirection.Descending),
            _ => new(nameof(LibraryEntryViewModel.Name), ListSortDirection.Ascending),
        };
        EntriesView.SortDescriptions.Add(description);
    }

    [ObservableProperty]
    private LibraryEntryViewModel? _selectedEntry;

    partial void OnSelectedEntryChanged(LibraryEntryViewModel? value)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = ReferenceEquals(entry, value);
        }
    }

    [RelayCommand]
    private void SelectEntry(LibraryEntryViewModel? entry)
    {
        if (entry is not null)
        {
            SelectedEntry = entry;
        }
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Готово";

    public bool HasNoEntries => Entries.Count == 0;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoEntries));
    }

    public async Task InitializeAsync()
    {
        var index = await _library.LoadAsync().ConfigureAwait(true);
        foreach (var entry in index.Entries)
        {
            var manifest = await _library.LoadManifestAsync(entry).ConfigureAwait(true);
            Entries.Add(new LibraryEntryViewModel(entry, manifest));
        }

        SelectedEntry = Entries.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoEntries));
        StatusText = Entries.Count == 0
            ? "Библиотека пуста — добавьте сборку"
            : $"Сборок в библиотеке: {Entries.Count}";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку сборки",
        };

        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        await ImportFolderAsync(dialog.FolderName).ConfigureAwait(true);
    }

    public async Task ImportFolderAsync(string folderPath)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Сканирование…";
        try
        {
            var entry = await _library.ImportAsync(folderPath).ConfigureAwait(true);
            var manifest = await _library.LoadManifestAsync(entry).ConfigureAwait(true);
            var viewModel = new LibraryEntryViewModel(entry, manifest);
            Entries.Add(viewModel);
            SelectedEntry = viewModel;
            StatusText = $"Сборок в библиотеке: {Entries.Count}";
            _snackbar.Show(
                "Библиотека",
                $"Сборка «{viewModel.Name}» добавлена",
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Import rejected: {Message}", exception.Message);
            _snackbar.Show(
                "Импорт невозможен",
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Import failed for {Path}", folderPath);
            _snackbar.Show(
                "Ошибка импорта",
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RescanAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
            if (libraryEntry is null)
            {
                return;
            }

            var scan = await _library.RescanAsync(libraryEntry).ConfigureAwait(true);
            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            entry.UpdateManifest(manifest ?? new ModpackManifest());
            _snackbar.Show(
                "Ре-скан",
                $"Игра: {entry.GameDisplayName}, движок: {entry.EngineDisplayName}",
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
                TimeSpan.FromSeconds(4));
            _ = scan;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var index = await _library.LoadAsync().ConfigureAwait(true);
            var stored = index.Entries.FirstOrDefault(e => e.Id == entry.Id);
            if (stored is not null)
            {
                index.Entries.Remove(stored);
                await _library.SaveAsync(index).ConfigureAwait(true);
            }

            Entries.Remove(entry);
            if (SelectedEntry == entry)
            {
                SelectedEntry = Entries.FirstOrDefault();
            }

            StatusText = $"Сборок в библиотеке: {Entries.Count}";
            _snackbar.Show(
                "Библиотека",
                "Сборка убрана из библиотеки (папка не тронута)",
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPackFolder(LibraryEntryViewModel? entry)
    {
        if (entry is null || !Directory.Exists(entry.RootPath))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = entry.RootPath,
            UseShellExecute = true,
        });
    }

    private async Task<LibraryEntry?> FindEntryAsync(Guid id)
    {
        var index = await _library.LoadAsync().ConfigureAwait(true);
        return index.Entries.FirstOrDefault(e => e.Id == id);
    }
}

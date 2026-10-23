using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.App.Services;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Launch;
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
    private readonly ILaunchService _launch;
    private readonly ISnackbarService _snackbar;
    private readonly IContentDialogService _dialog;
    private readonly ILogWindowService _logWindow;
    private readonly ILogger _logger;

    public MainViewModel(
        ILibraryService library,
        ILaunchService launch,
        ISnackbarService snackbar,
        IContentDialogService dialog,
        ILogWindowService logWindow,
        ILogger logger)
    {
        _library = library;
        _launch = launch;
        _snackbar = snackbar;
        _dialog = dialog;
        _logWindow = logWindow;
        _logger = logger.ForContext<MainViewModel>();
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoEntries));
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        if (EntriesView is ListCollectionView listView && listView.CanChangeLiveSorting)
        {
            // Переименование меняет ключ сортировки — без live-sorting карточка остаётся на старом месте.
            listView.LiveSortingProperties.Add(nameof(LibraryEntryViewModel.Name));
            listView.LiveSortingProperties.Add(nameof(LibraryEntryViewModel.LastPlayedAtUtc));
            listView.LiveSortingProperties.Add(nameof(LibraryEntryViewModel.PlaytimeMinutes));
            listView.IsLiveSorting = true;
        }

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
    private void OpenLogWindow() => _logWindow.Show();

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
        try
        {
            var index = await _library.LoadAsync().ConfigureAwait(true);
            foreach (var entry in index.Entries)
            {
                var manifest = await _library.LoadManifestAsync(entry).ConfigureAwait(true);
                Entries.Add(new LibraryEntryViewModel(entry, manifest, this));
            }

            SelectedEntry = Entries.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoEntries));
            StatusText = "Готово";
        }
        catch (Exception exception)
        {
            // Библиотека не должна «молча пустеть»: пользователь видит сообщение, детали — в логе.
            _logger.Error(exception, "Library initialization failed");
            StatusText = "Библиотека не загружена";
            _snackbar.Show(
                "Библиотека",
                "Не удалось загрузить библиотеку — подробности в логе (меню «Логи»).",
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(8));
        }
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
            var viewModel = new LibraryEntryViewModel(entry, manifest, this);
            Entries.Add(viewModel);
            SelectedEntry = viewModel;
            StatusText = "Готово";

            // Post-import customization: name + cover (defaults = folder name / no cover).
            var (displayName, coverImagePath) = await ShowImportDialogAsync(viewModel.Name).ConfigureAwait(true);
            if (coverImagePath is not null)
            {
                await _library.SetCoverAsync(entry.RootPath, coverImagePath).ConfigureAwait(true);
                viewModel.Refresh();
            }

            if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(displayName.Trim(), entry.Name, StringComparison.Ordinal))
            {
                await _library.RenameAsync(entry, displayName).ConfigureAwait(true);
                viewModel.ApplyName(displayName.Trim());
            }

            // Подтверждение — на любой успешный импорт, а не только на переименованный.
            _snackbar.Show(
                "Библиотека",
                $"Сборка «{entry.Name}» добавлена",
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

            await _library.RescanAsync(libraryEntry).ConfigureAwait(true);
            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            entry.UpdateManifest(manifest ?? new ModpackManifest());
            _snackbar.Show(
                "Ре-скан",
                $"Игра: {entry.GameDisplayName}, движок: {entry.EngineDisplayName}",
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Re-scan rejected: {Message}", exception.Message);
            _snackbar.Show(
                "Ре-скан невозможен",
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Re-scan failed for {Path}", entry.RootPath);
            _snackbar.Show(
                "Ошибка ре-скана",
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
    private async Task LaunchAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Запуск…";
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
            var manifest = libraryEntry is null
                ? null
                : await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            if (manifest is null)
            {
                throw new InvalidOperationException("Манифест сборки не найден — выполните ре-скан.");
            }

            using var process = await _launch.LaunchAsync(manifest, entry.RootPath).ConfigureAwait(true);
            _logger.Information("Launched {Name} (pid={ProcessId})", entry.Name, process.Id);
            _snackbar.Show(
                "Запуск",
                $"Сборка «{entry.Name}» запущена",
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.Play24 },
                TimeSpan.FromSeconds(4));
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // UAC prompt refused — a cancel, not an error (FEHLauncher scheme).
            _logger.Information("Launch cancelled by user (UAC refused) for {Path}", entry.RootPath);
            _snackbar.Show(
                "Запуск отменён",
                "Запрос UAC отклонён — запуск без прав администратора невозможен.",
                ControlAppearance.Light,
                new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Launch rejected: {Message}", exception.Message);
            _snackbar.Show(
                "Запуск невозможен",
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Launch failed for {Path}", entry.RootPath);
            _snackbar.Show(
                "Ошибка запуска",
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
            StatusText = "Готово";
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        var removeDialog = CreateDialog(
            "Убрать сборку из библиотеки?",
            $"«{entry.Name}» будет убрана из списка IMM. Папка сборки на диске не удаляется.",
            "Убрать", "Отмена", SymbolRegular.Delete24, danger: true);
        var dialogResult = await _dialog.ShowAsync(removeDialog, CancellationToken.None).ConfigureAwait(true);

        if (dialogResult is not ContentDialogResult.Primary)
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

            StatusText = "Готово";
            _snackbar.Show(
                "Библиотека",
                "Сборка убрана из библиотеки (папка не тронута)",
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Removing entry {Id} failed", entry.Id);
            _snackbar.Show(
                "Библиотека",
                $"Не удалось убрать сборку: {exception.Message}",
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
    public void OpenPackFolder(LibraryEntryViewModel? entry)
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

    /// <summary>Persists a manually picked MO2 profile; survives re-scan (ProfileOverriddenByUser).</summary>
    public async Task SetProfileAsync(LibraryEntryViewModel entry, string profile)
    {
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
            if (libraryEntry is null)
            {
                return;
            }

            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            if (manifest is null)
            {
                return;
            }

            manifest.Mo2 ??= new Mo2Section();
            manifest.Mo2.ProfileName = profile;
            manifest.Detection.ProfileOverriddenByUser = true;
            await _library.SaveManifestAsync(entry.RootPath, manifest).ConfigureAwait(true);
            entry.UpdateManifest(manifest);

            _snackbar.Show(
                "Профиль",
                $"Для «{entry.Name}» выбран профиль «{profile}»",
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            // Метод вызывается fire-and-forget из команды карточки: ошибка обязана дойти до UI.
            _logger.Error(exception, "Saving profile {Profile} for {Path} failed", profile, entry.RootPath);
            _snackbar.Show(
                "Профиль",
                $"Не удалось сохранить профиль: {exception.Message}",
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    public void SetProfile(LibraryEntryViewModel entry, string profile) =>
        _ = SetProfileAsync(entry, profile);

    /// <summary>
    /// Manual game folder pick (FEHLauncher-style last resort): validates a supported game exe,
    /// updates the manifest (GameType, SystemGamePath, launch executable), flags the override.
    /// </summary>
    public async Task ChooseGameFolderAsync(LibraryEntryViewModel entry, string folder)
    {
        var gameType = Mo2Locator.DetectGameByExe(folder);
        if (gameType is null)
        {
            _snackbar.Show(
                "Папка не подходит",
                "В выбранной папке нет exe поддерживаемой игры (Skyrim LE/SE, Fallout 4/NV).",
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
            return;
        }

        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
            if (libraryEntry is null)
            {
                return;
            }

            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            if (manifest is null)
            {
                return;
            }

            manifest.GameType = gameType.Value;
            manifest.SystemGamePath = folder;
            manifest.Source = PackSource.System;
            manifest.Launch.Executable = Mo2Locator.DetectLaunchExecutableName(folder) ?? string.Empty;
            manifest.Detection.OverriddenByUser = true;
            await _library.SaveManifestAsync(entry.RootPath, manifest).ConfigureAwait(true);
            entry.UpdateManifest(manifest);

            _snackbar.Show(
                "Игра",
                $"Для «{entry.Name}» указана папка: {folder}",
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Saving game folder {Folder} for {Path} failed", folder, entry.RootPath);
            _snackbar.Show(
                "Игра",
                $"Не удалось сохранить папку игры: {exception.Message}",
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    public void ChooseGameFolder(LibraryEntryViewModel entry)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с игрой",
        };

        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        _ = ChooseGameFolderAsync(entry, dialog.FolderName);
    }

    /// <summary>
    /// IMM-styled dialog factory (design/DESIGN.md canon): navy card content, canon accent on the
    /// primary button, themed icons. All app dialogs go through this to stay visually consistent.
    /// </summary>
    private ContentDialog CreateDialog(string title, object content, string primaryText,
        string closeText, SymbolRegular primaryIcon, bool danger = false)
    {
        return new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            PrimaryButtonIcon = new SymbolIcon { Symbol = primaryIcon },
            CloseButtonAppearance = ControlAppearance.Secondary,
            DialogWidth = 460,
        };
    }

    /// <summary>Post-import dialog: pack name + optional cover image. Cancel keeps the defaults.</summary>
    private async Task<(string? DisplayName, string? CoverImagePath)> ShowImportDialogAsync(string defaultName)
    {
        var textBox = new Wpf.Ui.Controls.TextBox
        {
            Text = defaultName,
            PlaceholderText = "Название сборки",
            Margin = new System.Windows.Thickness(0, 4, 0, 4),
        };
        var coverButton = new Wpf.Ui.Controls.Button
        {
            Content = "Выбрать обложку…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Image24 },
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        var coverText = new System.Windows.Controls.TextBlock
        {
            Text = "Обложка не выбрана (PNG/JPG/WebP)",
            Opacity = 0.7,
            Margin = new System.Windows.Thickness(0, 4, 0, 0),
        };
        string? coverPath = null;
        coverButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите обложку",
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() is true)
            {
                coverPath = dialog.FileName;
                coverText.Text = dialog.FileName;
            }
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Название:",
            Opacity = 0.8,
        });
        panel.Children.Add(textBox);
        panel.Children.Add(coverButton);
        panel.Children.Add(coverText);

        var dialog = CreateDialog("Новая сборка", panel, "Сохранить", "Пропустить", SymbolRegular.CheckmarkCircle24);
        var result = await _dialog.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true);

        return result is ContentDialogResult.Primary
            ? (textBox.Text, coverPath)
            : (null, null);
    }

    /// <summary>Menu action: rename the pack (library.json), empty name keeps the old one.</summary>
    public async Task RenameEntryAsync(LibraryEntryViewModel entry)
    {
        var textBox = new Wpf.Ui.Controls.TextBox
        {
            Text = entry.Name,
            PlaceholderText = "Название сборки",
            Margin = new System.Windows.Thickness(0, 4, 0, 4),
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Название:",
            Opacity = 0.8,
        });
        panel.Children.Add(textBox);

        var dialog = CreateDialog("Переименовать сборку", panel, "Сохранить", "Отмена", SymbolRegular.CheckmarkCircle24);
        var result = await _dialog.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true);

        if (result is not ContentDialogResult.Primary || string.IsNullOrWhiteSpace(textBox.Text))
        {
            return;
        }

        var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
        if (libraryEntry is null)
        {
            return;
        }

        await _library.RenameAsync(libraryEntry, textBox.Text).ConfigureAwait(true);
        entry.ApplyName(textBox.Text.Trim());
    }

    public void RenameEntry(LibraryEntryViewModel entry) => _ = RenameEntryAsync(entry);

    /// <summary>Menu action: pick a new cover image, copied into the pack root as cover.*</summary>
    public async Task ChangeCoverAsync(LibraryEntryViewModel entry)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите обложку",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() is not true)
        {
            return;
        }

        try
        {
            await _library.SetCoverAsync(entry.RootPath, dialog.FileName).ConfigureAwait(true);
            entry.Refresh();
            _snackbar.Show(
                "Обложка",
                $"Обложка «{entry.Name}» обновлена",
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Cover change failed for {Path}", entry.RootPath);
            _snackbar.Show(
                "Обложка",
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    public void ChangeCover(LibraryEntryViewModel entry) => _ = ChangeCoverAsync(entry);

    private async Task<LibraryEntry?> FindEntryAsync(Guid id)
    {
        var index = await _library.LoadAsync().ConfigureAwait(true);
        return index.Entries.FirstOrDefault(e => e.Id == id);
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.App.Services;
using Imm.Core.Backups;
using Imm.Core.Diagnostics;
using Imm.Core.Domain;
using Imm.Core.IO;
using Imm.Core.Launch;
using Imm.Core.Library;
using Imm.Core.Scanning;
using Imm.Core.Settings;
using Imm.Core.Tracking;
using Microsoft.Win32;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using Imm.Core.Localization;

namespace Imm.App.ViewModels;

/// <summary>Library page state: modpack cards, selection, import (IMM.md MVP #1).</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILibraryService _library;
    private readonly ILaunchService _launch;
    private readonly IBackupService _backups;
    private readonly ISessionTracker _sessionTracker;
    private readonly IWindowActivationService _windowActivation;
    private readonly IPackDiagnosticsService _diagnostics;
    private readonly IAppSettingsService _appSettings;
    private readonly ImmPaths _paths;
    private readonly ISnackbarService _snackbar;
    private readonly IContentDialogService _dialog;
    private readonly ILogWindowService _logWindow;
    private readonly ILogger _logger;

    private AppSettings _settings = new();
    private bool _settingsLoaded;

    public MainViewModel(
        ILibraryService library,
        ILaunchService launch,
        IBackupService backups,
        ISessionTracker sessionTracker,
        IWindowActivationService windowActivation,
        IPackDiagnosticsService diagnostics,
        IAppSettingsService appSettings,
        ImmPaths paths,
        ISnackbarService snackbar,
        IContentDialogService dialog,
        ILogWindowService logWindow,
        ILogger logger)
    {
        _library = library;
        _launch = launch;
        _backups = backups;
        _sessionTracker = sessionTracker;
        _windowActivation = windowActivation;
        _diagnostics = diagnostics;
        _appSettings = appSettings;
        _paths = paths;
        _snackbar = snackbar;
        _dialog = dialog;
        _logWindow = logWindow;
        _logger = logger.ForContext<MainViewModel>();
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoEntries));
            OnPropertyChanged(nameof(PacksCountText));
            OnPropertyChanged(nameof(PacksInLibraryText));
        };

        // A language switch must repaint the strings this VM produced itself.
        Loc.Instance.PropertyChanged += (_, _) => OnLanguageChanged();
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

    /// <summary>Rebuilt per language: a stored list would keep the previous language forever.</summary>
    public IReadOnlyList<string> SortOptions => [Loc.T("Lib_SortName"), Loc.T("Lib_SortDate"), Loc.T("Lib_SortTime")];

    /// <summary>Index, not the label — the label is language-dependent.</summary>
    private int _sortIndex;

    [ObservableProperty]
    private string _selectedSortOption = Loc.T("Lib_SortName");

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

    // ===== Left-nav sections: Library / Scanner / Settings =====

    [ObservableProperty]
    private AppSection _activeSection = AppSection.Library;

    public bool IsLibrarySection => ActiveSection == AppSection.Library;

    public bool IsScannerSection => ActiveSection == AppSection.Scanner;

    public bool IsSettingsSection => ActiveSection == AppSection.Settings;

    partial void OnActiveSectionChanged(AppSection value)
    {
        OnPropertyChanged(nameof(IsLibrarySection));
        OnPropertyChanged(nameof(IsScannerSection));
        OnPropertyChanged(nameof(IsSettingsSection));

        // Свежая диагностика на входе в раздел: факты на диске меняются без участия IMM.
        if (value == AppSection.Scanner)
        {
            _ = RefreshDiagnosticsAsync();
        }
    }

    [RelayCommand]
    private void ShowSection(AppSection section) => ActiveSection = section;

    // ===== Scanner screen: what is detected vs what the manifest claims =====

    public ObservableCollection<PackDiagnosticsViewModel> Diagnostics { get; } = [];

    [ObservableProperty]
    private bool _isDiagnosticsBusy;

    [ObservableProperty]
    private string _diagnosticsStatus = Loc.T("Scan_StatusNever");

    public bool HasNoDiagnostics => Diagnostics.Count == 0;

    [RelayCommand]
    private async Task RefreshDiagnosticsAsync()
    {
        if (IsDiagnosticsBusy)
        {
            return;
        }

        IsDiagnosticsBusy = true;
        DiagnosticsStatus = Loc.T("Scan_StatusBusy");
        try
        {
            var snapshot = Entries.ToArray();
            var results = new List<PackDiagnosticsViewModel>(snapshot.Length);
            foreach (var entry in snapshot)
            {
                var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
                if (libraryEntry is null)
                {
                    continue;
                }

                var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
                var diagnostics = await _diagnostics.AnalyzeAsync(libraryEntry, manifest).ConfigureAwait(true);
                results.Add(new PackDiagnosticsViewModel(entry, diagnostics));
            }

            Diagnostics.Clear();
            foreach (var result in results)
            {
                Diagnostics.Add(result);
            }

            DiagnosticsStatus = results.Count == 0
                ? Loc.T("Scan_StatusEmpty")
                : Loc.F("Scan_StatusDone", results.Count, results.Count(result => result.HasProblems));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Diagnostics failed");
            DiagnosticsStatus = Loc.T("Scan_StatusFailed");
            _snackbar.Show(
                Loc.T("Scan_Title"),
                Loc.F("Scan_Failed", exception.Message),
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsDiagnosticsBusy = false;
            OnPropertyChanged(nameof(HasNoDiagnostics));
        }
    }

    // ===== Settings screen (settings.json next to library.json) =====

    [ObservableProperty]
    private int _backupsLimit;

    partial void OnBackupsLimitChanged(int value)
    {
        if (_settingsLoaded)
        {
            _ = SaveSettingsAsync();
        }
    }

    [ObservableProperty]
    private string _settingsStatus = string.Empty;

    // ===== Language =====

    /// <summary>Endonyms on purpose: the picker must be readable whatever language is active.</summary>
    public IReadOnlyList<string> Languages { get; } = ["English", "Русский"];

    [ObservableProperty]
    private string _selectedLanguage = "English";

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        var code = string.Equals(value, "Русский", StringComparison.Ordinal) ? Loc.Russian : Loc.English;
        _settings.Language = code;
        Loc.Instance.SetLanguage(code);
        _ = SaveSettingsAsync();
    }

    /// <summary>Keeps the chosen sort order across a language switch and repaints VM strings.</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SortOptions));
        SelectedSortOption = SortOptions[Math.Clamp(_sortIndex, 0, SortOptions.Count - 1)];
        ApplySort();

        // Card texts are computed properties on their own VMs — they need their own ping.
        foreach (var entry in Entries)
        {
            entry.NotifyLocalizationChanged();
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>Status bar text; stored as a resource key so a language switch repaints it.</summary>
    public string StatusText => Loc.T(_statusKey);

    private void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Localized counts for the header and the sidebar footer.</summary>
    public string PacksCountText => Loc.PacksCount(Entries.Count);

    public string PacksInLibraryText => $"{PacksCountText} {Loc.T("Plural_PacksInLibrary")}";

    /// <summary>Archives live in Documents\&lt;packId&gt; — this is the shared root (IMM.md «Бэкапы»).</summary>
    public string BackupsFolderPath => _paths.DocumentsDirectory;

    public string LogsFolderPath => _paths.LogsDirectory;

    public string LibraryFilePath => _paths.LibraryFilePath;

    public string LibraryFolderPath => Path.GetDirectoryName(_paths.LibraryFilePath) ?? _paths.LibraryFilePath;

    public string LibraryModeText => _paths.Portable
        ? Loc.T("Set_ModePortable")
        : Loc.T("Set_ModeStandard");

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _appSettings.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Loading settings failed; defaults used");
            _settings = new AppSettings();
        }

        // Загрузка не должна тут же писать файл обратно.
        _settingsLoaded = false;
        BackupsLimit = Math.Max(0, _settings.MaxBackups);
        SelectedLanguage = string.Equals(_settings.Language, Loc.Russian, StringComparison.OrdinalIgnoreCase)
            ? "Русский"
            : "English";
        Loc.Instance.SetLanguage(_settings.Language);
        _settingsLoaded = true;

        OnPropertyChanged(nameof(PacksCountText));
        OnPropertyChanged(nameof(PacksInLibraryText));

        OnPropertyChanged(nameof(BackupsFolderPath));
        OnPropertyChanged(nameof(LogsFolderPath));
        OnPropertyChanged(nameof(LibraryFilePath));
        OnPropertyChanged(nameof(LibraryFolderPath));
        OnPropertyChanged(nameof(LibraryModeText));
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            _settings.MaxBackups = Math.Max(0, BackupsLimit);
            await _appSettings.SaveAsync(_settings).ConfigureAwait(true);
            SettingsStatus = Loc.T("Set_Saved");
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Saving settings failed");
            SettingsStatus = Loc.T("Set_NotSaved");
            _snackbar.Show(
                Loc.T("Set_Title"),
                Loc.F("Set_SaveFailed", exception.Message),
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>Opens a folder in Explorer (settings rows).</summary>
    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LongPath.Normalize(path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Opening folder {Path} failed", path);
            _snackbar.Show(
                Loc.T("Msg_Explorer"),
                Loc.F("Msg_OpenFolderFailed", exception.Message),
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    [RelayCommand]
    private void SetGridView() => IsGridView = true;

    [RelayCommand]
    private void SetListView() => IsGridView = false;

    partial void OnSelectedSortOptionChanged(string value)
    {
        var index = IndexOfSortOption(value);
        if (index >= 0)
        {
            _sortIndex = index;
        }

        ApplySort();
    }

    private int IndexOfSortOption(string? value)
    {
        var options = SortOptions;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void ApplySort()
    {
        EntriesView.SortDescriptions.Clear();
        SortDescription description = _sortIndex switch
        {
            1 => new(nameof(LibraryEntryViewModel.LastPlayedAtUtc), ListSortDirection.Descending),
            2 => new(nameof(LibraryEntryViewModel.PlaytimeMinutes), ListSortDirection.Descending),
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

    private string _statusKey = "Common_Done";

    public bool HasNoEntries => Entries.Count == 0;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoEntries));
    }

    public async Task InitializeAsync()
    {
        await LoadSettingsAsync().ConfigureAwait(true);
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
            SetStatus("Common_Done");
        }
        catch (Exception exception)
        {
            // Библиотека не должна «молча пустеть»: пользователь видит сообщение, детали — в логе.
            _logger.Error(exception, "Library initialization failed");
            SetStatus("Msg_LibraryNotLoaded");
            _snackbar.Show(
                Loc.T("Msg_Library"),
                Loc.T("Msg_LibraryLoadFailed"),
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
            Title = Loc.T("Msg_ChoosePackFolder"),
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
        SetStatus("Msg_Scanning");
        try
        {
            var entry = await _library.ImportAsync(folderPath).ConfigureAwait(true);
            var manifest = await _library.LoadManifestAsync(entry).ConfigureAwait(true);
            var viewModel = new LibraryEntryViewModel(entry, manifest, this);
            Entries.Add(viewModel);
            SelectedEntry = viewModel;
            SetStatus("Common_Done");

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
                Loc.T("Msg_Library"),
                Loc.F("Msg_PackAdded", entry.Name),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Import rejected: {Message}", exception.Message);
            _snackbar.Show(
                Loc.T("Msg_ImportRejected"),
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Import failed for {Path}", folderPath);
            _snackbar.Show(
                Loc.T("Msg_ImportError"),
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
                Loc.T("Msg_Rescan"),
                Loc.F("Msg_RescanDone", entry.GameDisplayName, entry.EngineDisplayName),
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
                TimeSpan.FromSeconds(4));

            // Экран сканера показывает сравнение с манифестом — после ре-скана оно устарело.
            if (ActiveSection == AppSection.Scanner)
            {
                await RefreshDiagnosticsAsync().ConfigureAwait(true);
            }
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Re-scan rejected: {Message}", exception.Message);
            _snackbar.Show(
                Loc.T("Msg_RescanRejected"),
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Re-scan failed for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_RescanError"),
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
        SetStatus("Msg_Launching");
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true);
            var manifest = libraryEntry is null
                ? null
                : await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true);
            if (manifest is null)
            {
                throw new InvalidOperationException(Loc.T("Msg_ManifestMissing"));
            }

            var launchStartedUtc = DateTime.UtcNow;
            var process = await _launch.LaunchAsync(manifest, entry.RootPath).ConfigureAwait(true);
            _logger.Information("Launched {Name} (pid={ProcessId})", entry.Name, process.Id);
            TrackSession(entry, manifest, process, launchStartedUtc);
            _snackbar.Show(
                Loc.T("Msg_Launch"),
                Loc.F("Msg_Launched", entry.Name),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.Play24 },
                TimeSpan.FromSeconds(4));
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // UAC prompt refused — a cancel, not an error (FEHLauncher scheme).
            _logger.Information("Launch cancelled by user (UAC refused) for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_LaunchCancelled"),
                Loc.T("Msg_UacLaunchDenied"),
                ControlAppearance.Light,
                new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Launch rejected: {Message}", exception.Message);
            _snackbar.Show(
                Loc.T("Msg_LaunchRejected"),
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Launch failed for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_LaunchError"),
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
            SetStatus("Common_Done");
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
            Loc.T("Msg_RemoveTitle"),
            Loc.F("Msg_RemoveBody", entry.Name),
            Loc.T("Msg_Remove"), Loc.T("Common_Cancel"), SymbolRegular.Delete24, danger: true);
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

            SetStatus("Common_Done");
            _snackbar.Show(
                Loc.T("Msg_Library"),
                Loc.T("Msg_Removed"),
                ControlAppearance.Info,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Removing entry {Id} failed", entry.Id);
            _snackbar.Show(
                Loc.T("Msg_Library"),
                Loc.F("Msg_RemoveFailed", exception.Message),
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
                Loc.T("Msg_Profile"),
                Loc.F("Msg_ProfileSet", entry.Name, profile),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            // Метод вызывается fire-and-forget из команды карточки: ошибка обязана дойти до UI.
            _logger.Error(exception, "Saving profile {Profile} for {Path} failed", profile, entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_Profile"),
                Loc.F("Msg_ProfileFailed", exception.Message),
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
                Loc.T("Msg_FolderRejected"),
                Loc.T("Msg_FolderRejectedBody"),
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
                Loc.T("Msg_Game"),
                Loc.F("Msg_GameFolderSet", entry.Name, folder),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Saving game folder {Folder} for {Path} failed", folder, entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_Game"),
                Loc.F("Msg_GameFolderFailed", exception.Message),
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    public void ChooseGameFolder(LibraryEntryViewModel entry)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Msg_ChooseGameFolder"),
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
            PlaceholderText = Loc.T("Msg_PackNamePlaceholder"),
            Margin = new System.Windows.Thickness(0, 4, 0, 4),
        };
        var coverButton = new Wpf.Ui.Controls.Button
        {
            Content = Loc.T("Msg_ChooseCover"),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Image24 },
            Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        var coverText = new System.Windows.Controls.TextBlock
        {
            Text = Loc.T("Msg_NoCover"),
            Opacity = 0.7,
            Margin = new System.Windows.Thickness(0, 4, 0, 0),
        };
        string? coverPath = null;
        coverButton.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("Msg_ChooseCoverTitle"),
                Filter = Loc.T("Msg_ImageFilter"),
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
            Text = Loc.T("Common_NameLabel"),
            Opacity = 0.8,
        });
        panel.Children.Add(textBox);
        panel.Children.Add(coverButton);
        panel.Children.Add(coverText);

        var dialog = CreateDialog(Loc.T("Msg_NewPack"), panel, Loc.T("Common_Save"), Loc.T("Common_Skip"), SymbolRegular.CheckmarkCircle24);
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
            PlaceholderText = Loc.T("Msg_PackNamePlaceholder"),
            Margin = new System.Windows.Thickness(0, 4, 0, 4),
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = Loc.T("Common_NameLabel"),
            Opacity = 0.8,
        });
        panel.Children.Add(textBox);

        var dialog = CreateDialog(Loc.T("Msg_RenameTitle"), panel, Loc.T("Common_Save"), Loc.T("Common_Cancel"), SymbolRegular.CheckmarkCircle24);
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
            Title = Loc.T("Msg_ChooseCoverTitle"),
            Filter = Loc.T("Msg_ImageFilter"),
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
                Loc.T("Msg_Cover"),
                Loc.F("Msg_CoverUpdated", entry.Name),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24 },
                TimeSpan.FromSeconds(4));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Cover change failed for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_Cover"),
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    public void ChangeCover(LibraryEntryViewModel entry) => _ = ChangeCoverAsync(entry);

    /// <summary>Manual backup (IMM.md «Бэкапы»): zip of the MO2 profile or of the game saves.</summary>
    [RelayCommand]
    private async Task CreateBackupAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("Msg_BackupBusy");
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true)
                ?? throw new InvalidOperationException(Loc.T("Msg_PackNotFound"));
            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true)
                ?? throw new InvalidOperationException(Loc.T("Msg_ManifestMissing"));

            var archive = await _backups
                .CreateBackupAsync(libraryEntry, manifest, retentionOverride: _settings.MaxBackups)
                .ConfigureAwait(true);
            _snackbar.Show(
                Loc.T("Msg_Backup"),
                Loc.F("Msg_BackupSaved", Path.GetFileName(archive)),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.Archive24 },
                TimeSpan.FromSeconds(6));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Backup rejected: {Message}", exception.Message);
            _snackbar.Show(
                Loc.T("Msg_BackupRejected"),
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Backup failed for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_BackupError"),
                exception.Message,
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsBusy = false;
            SetStatus("Common_Done");
        }
    }

    /// <summary>Opens the pack's backup folder (Documents\&lt;packId&gt;), creating it when missing.</summary>
    [RelayCommand]
    private void OpenBackupsFolder(LibraryEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            var directory = _backups.PackDirectory(entry.Id);
            Directory.CreateDirectory(LongPath.Normalize(directory));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Opening backups folder for {Path} failed", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_Backups"),
                Loc.F("Msg_OpenFolderFailed", exception.Message),
                ControlAppearance.Danger,
                new SymbolIcon { Symbol = SymbolRegular.Dismiss24 },
                TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>
    /// «Открыть MO2»: brings the running MO2 window to the front, otherwise starts the instance without
    /// a game. A second MO2 on the same instance is never started — it would corrupt the profile.
    /// </summary>
    [RelayCommand]
    private async Task OpenMo2Async(LibraryEntryViewModel? entry)
    {
        if (entry is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var libraryEntry = await FindEntryAsync(entry.Id).ConfigureAwait(true)
                ?? throw new InvalidOperationException(Loc.T("Msg_PackNotFound"));
            var manifest = await _library.LoadManifestAsync(libraryEntry).ConfigureAwait(true)
                ?? throw new InvalidOperationException(Loc.T("Msg_ManifestMissing"));

            var instance = Mo2Locator.ResolveInstance(libraryEntry.RootPath, manifest.Mo2?.Directory)
                ?? throw new InvalidOperationException(
                    Loc.T("Msg_Mo2ExeMissing"));
            var mo2Exe = Path.Combine(instance, "ModOrganizer.exe");

            if (_windowActivation.TryActivate(mo2Exe))
            {
                _logger.Information("Mod Organizer window activated for {Path}", entry.RootPath);
                _snackbar.Show(
                    "MO2",
                    Loc.T("Msg_Mo2Activated"),
                    ControlAppearance.Info,
                    new SymbolIcon { Symbol = SymbolRegular.Window24 },
                    TimeSpan.FromSeconds(4));
                return;
            }

            await _launch.OpenModOrganizerAsync(manifest, libraryEntry.RootPath).ConfigureAwait(true);
            _snackbar.Show(
                "MO2",
                Loc.T("Msg_Mo2Started"),
                ControlAppearance.Success,
                new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TimeSpan.FromSeconds(4));
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _logger.Information("Opening MO2 cancelled by user (UAC refused) for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_LaunchCancelled"),
                Loc.T("Msg_UacMo2Denied"),
                ControlAppearance.Light,
                new SymbolIcon { Symbol = SymbolRegular.Info24 },
                TimeSpan.FromSeconds(4));
        }
        catch (InvalidOperationException exception)
        {
            _logger.Warning("Opening MO2 rejected: {Message}", exception.Message);
            _snackbar.Show(
                Loc.T("Msg_Mo2NotOpened"),
                exception.Message,
                ControlAppearance.Caution,
                new SymbolIcon { Symbol = SymbolRegular.Warning24 },
                TimeSpan.FromSeconds(6));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Opening MO2 failed for {Path}", entry.RootPath);
            _snackbar.Show(
                Loc.T("Msg_Mo2LaunchError"),
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

    /// <summary>
    /// Session tracking in the background (IMM.md «Трекинг сессии»): the UI never waits for the game to
    /// close, and a tracking failure must not affect the launch result.
    /// </summary>
    /// <summary>Session in flight: IMM.md writes the result «по завершении сессии и при выходе».</summary>
    private (Guid EntryId, DateTime StartedUtc)? _openSession;

    private void TrackSession(LibraryEntryViewModel entry, ModpackManifest manifest,
        System.Diagnostics.Process process, DateTime launchStartedUtc)
    {
        var expectedGameDirectory = manifest.Source == PackSource.System ? manifest.SystemGamePath : null;
        _openSession = (entry.Id, launchStartedUtc);

        _ = Task.Run(async () =>
        {
            try
            {
                var session = await _sessionTracker
                    .TrackAsync(manifest, launchStartedUtc, process, expectedGameDirectory)
                    .ConfigureAwait(false);
                if (session is null)
                {
                    return;
                }

                var stored = await _library
                    .RecordSessionAsync(entry.Id, session.PlaytimeMinutes, session.LastPlayedAtUtc)
                    .ConfigureAwait(false);
                if (stored is null)
                {
                    return;
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    entry.ApplySession(stored.PlaytimeMinutes, stored.LastPlayedAtUtc);
                    _logger.Information("Session recorded for {Name}: +{Minutes} min (total {Total})",
                        entry.Name, session.PlaytimeMinutes, stored.PlaytimeMinutes);
                });
            }
            catch (Exception exception)
            {
                _logger.Warning(exception, "Session tracking failed for {Path}", entry.RootPath);
            }
            finally
            {
                _openSession = null;
                process.Dispose();
            }
        });
    }

    /// <summary>
    /// IMM.md: the result is written «по завершении сессии и при выходе». Closing IMM mid-game must not
    /// throw the elapsed time away, so the open session is flushed synchronously on shutdown.
    /// </summary>
    public void FlushOpenSession()
    {
        if (_openSession is not { } open)
        {
            return;
        }

        _openSession = null;
        var minutes = Math.Max(0, (int)Math.Round((DateTime.UtcNow - open.StartedUtc).TotalMinutes));

        try
        {
            // Shutdown path: the host is about to stop, so the write cannot be awaited.
            _library.RecordSessionAsync(open.EntryId, minutes, DateTime.UtcNow).GetAwaiter().GetResult();
            _logger.Information("Open session flushed on exit: +{Minutes} min", minutes);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Flushing the open session on exit failed");
        }
    }

    private async Task<LibraryEntry?> FindEntryAsync(Guid id)
    {
        var index = await _library.LoadAsync().ConfigureAwait(true);
        return index.Entries.FirstOrDefault(e => e.Id == id);
    }
}

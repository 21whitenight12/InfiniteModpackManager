using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imm.Core.Logging;

namespace Imm.App.ViewModels;

/// <summary>
/// Live log view model: preloads the sink snapshot, then appends on the UI thread.
/// Bounded by the sink capacity; pause stops UI updates without touching logging itself.
/// Singleton lifetime — the sink subscription lives as long as the process.
/// </summary>
public partial class LogViewModel : ObservableObject
{
    private readonly UiLogSink _sink;

    public LogViewModel(UiLogSink sink)
    {
        _sink = sink;

        foreach (var line in sink.Snapshot())
        {
            Lines.Add(line);
        }

        _sink.LineAppended += OnLineAppended;
    }

    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>Запас сверх лимита: тримминг пачкой дешевле, чем RemoveAt(0) на каждую строку.</summary>
    private const int TrimSlack = 512;

    [ObservableProperty]
    private bool _isPaused;

    public string PauseButtonText => IsPaused ? "Продолжить" : "Пауза";

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Copy()
    {
        if (Lines.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Lines));
        }
    }

    [RelayCommand]
    private void ClearView() => Lines.Clear();

    private void OnLineAppended(string line)
    {
        if (IsPaused)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append(line);
        }
        else
        {
            dispatcher.BeginInvoke(() => Append(line));
        }
    }

    private void Append(string line)
    {
        Lines.Add(line);
        if (Lines.Count <= UiLogSink.DefaultCapacity + TrimSlack)
        {
            return;
        }

        // Пачкой, а не по строке: RemoveAt(0) на каждую строку — O(n²) при лог-шторме.
        for (var i = 0; i < TrimSlack; i++)
        {
            Lines.RemoveAt(0);
        }
    }
}

using System.Windows.Controls;
using Imm.App.ViewModels;
using Wpf.Ui.Controls;

namespace Imm.App.Views;

/// <summary>
/// Live log viewer: virtualized console-style list, auto-scroll while pinned to the bottom,
/// pause stops UI updates (file logging continues). Non-modal tool window for testing sessions.
/// </summary>
public partial class LogWindow : FluentWindow
{
    private readonly LogViewModel _viewModel;

    public LogWindow(LogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLogScrollChanged));
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.IsPaused || LogList.Items.Count == 0)
        {
            return;
        }

        // At (or near) the bottom → follow the tail; user scrolled up → stop auto-scrolling.
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 8)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}

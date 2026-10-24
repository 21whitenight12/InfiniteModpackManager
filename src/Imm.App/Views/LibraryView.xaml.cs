using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Imm.App.ViewModels;

namespace Imm.App.Views;

/// <summary>Library view (concept canon): card grid + details pane + import.</summary>
public partial class LibraryView : Page
{
    public LibraryView(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
        PacksList.SizeChanged += OnPacksSizeChanged;
    }

    /// <summary>Cards stretch to fill the row: columns follow the available width (min ~250px each).</summary>
    private void OnPacksSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || e.NewSize.Width <= 0)
        {
            return;
        }

        const double gap = 14;
        const double minCardWidth = 215;
        var columns = Math.Clamp((int)((e.NewSize.Width + gap) / (minCardWidth + gap)), 1, 4);

        // Slot = whole number of cells that fit exactly; the card itself leaves the gap as trailing margin.
        var slot = Math.Floor(e.NewSize.Width / columns);

        viewModel.CardColumns = columns;
        viewModel.CardSlotWidth = slot;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // Drop всплывает: без Handled тот же дроп поймает ещё и обработчик окна → двойной импорт.
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || DataContext is not MainViewModel vm)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (System.IO.Directory.Exists(path))
            {
                await vm.ImportFolderAsync(path);
            }
        }
    }

    /// <summary>Left click on the '...' button opens its context menu (placement under the button).</summary>
    private void OpenCardMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

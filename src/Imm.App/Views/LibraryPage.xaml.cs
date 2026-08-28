using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Imm.App.ViewModels;

namespace Imm.App.Views;

/// <summary>Library page (design canon): card grid + details pane + import.</summary>
public partial class LibraryPage : Page
{
    public LibraryPage(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
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
}

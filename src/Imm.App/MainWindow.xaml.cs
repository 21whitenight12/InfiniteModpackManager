using System.Windows;
using System.Windows.Input;
using Imm.App.ViewModels;
using Imm.App.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Imm.App;

/// <summary>
/// Main Fluent shell (IMM.md WPF UI rules). Hosts the library page; ContentDialog and
/// Snackbar hosts are wired into the singleton services.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(
        MainViewModel viewModel,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService)
    {
        InitializeComponent();
        ViewModel = viewModel;
        contentDialogService.SetDialogHost(RootContentDialogHost);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);
        PageHost.Content = new LibraryPage(viewModel);
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    public MainViewModel ViewModel { get; }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (System.IO.Directory.Exists(path))
            {
                await ViewModel.ImportFolderAsync(path);
            }
        }
    }
}

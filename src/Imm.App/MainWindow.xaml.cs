using Imm.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Imm.App;

/// <summary>
/// Main Fluent shell (IMM.md WPF UI rules): backdrop Auto + FallbackColor,
/// ContentDialog and Snackbar hosts wired into singleton services.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(
        MainViewModel viewModel,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService)
    {
        InitializeComponent();
        DataContext = viewModel;
        contentDialogService.SetDialogHost(RootContentDialogHost);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);
    }
}

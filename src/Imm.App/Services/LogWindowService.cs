using Imm.App.ViewModels;
using Imm.App.Views;

namespace Imm.App.Services;

/// <summary>Opens the single log tool window; re-activates it when already visible.</summary>
public interface ILogWindowService
{
    void Show();
}

public sealed class LogWindowService(LogViewModel viewModel) : ILogWindowService
{
    private LogWindow? _window;

    public void Show()
    {
        if (_window is { IsLoaded: true })
        {
            _window.Activate();
            return;
        }

        _window = new LogWindow(viewModel);
        _window.Show();
        _window.Activate();
    }
}

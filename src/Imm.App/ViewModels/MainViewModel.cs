using CommunityToolkit.Mvvm.ComponentModel;

namespace Imm.App.ViewModels;

/// <summary>Placeholder shell ViewModel; Phase 1 adds the modpack library.</summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Ready. Awaiting library bootstrap.";
}

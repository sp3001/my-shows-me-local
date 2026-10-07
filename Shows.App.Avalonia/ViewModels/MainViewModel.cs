using CommunityToolkit.Mvvm.ComponentModel;

namespace Shows.App.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Welcome to Avalonia!";
}

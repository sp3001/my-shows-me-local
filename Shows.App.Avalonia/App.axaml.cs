using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Shows.App.Avalonia.ViewModels;
using Shows.App.Avalonia.Views;

namespace Shows.App.Avalonia;

// Inside `namespace Shows.App.Avalonia`, the bare name `Application` binds to the sibling namespace
// Shows.Application (our Application project, referenced transitively) rather than Avalonia's type —
// CS0118. Namespace-member lookup outranks a file-level using alias, so the base type is fully qualified
// rather than aliased (the same collision the MAUI head's App.xaml.cs documents).
public partial class App : global::Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The shell view model (tabs + navigation stack) is resolved from the container built in
            // Program.Main; its ViewModels pull their services from the same container. This is the only
            // place the UI reaches the container besides Program (ARCHITECTURE.md §2).
            var shell = Program.Services.GetRequiredService<ShellViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = shell,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
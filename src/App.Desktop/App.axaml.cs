
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using App.Application.Composition;
using App.Desktop.ViewModels;
using App.Desktop.Views;
using App.Integrations.Terminal;

namespace App.Desktop;

public partial class App : Avalonia.Application
{
    private AppServices? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Racine de composition : tous les modules sont créés ici, une seule fois.
            _services = AppServices.Create();

            var vm = new MainWindowViewModel(_services);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            _ = vm.InitializeAsync();   // charge les extensions installées
            desktop.Exit += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

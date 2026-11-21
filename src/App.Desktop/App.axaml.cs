
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using App.Core.Contracts;
using App.Desktop.ViewModels;
using App.Desktop.Views;
using App.Integrations.Terminal;

namespace App.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ITerminalService terminalService = new TerminalService();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(terminalService)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

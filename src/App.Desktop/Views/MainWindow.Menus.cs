using System;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace App.Desktop.Views;

public partial class MainWindow
{
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AddHandler(KeyDownEvent, OnSettingsShortcut, RoutingStrategies.Tunnel);
    }

    // Ctrl+, ouvre les paramètres (comme dans VS Code)
    private void OnSettingsShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.Control)
        {
            OnOpenSettings(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnThemeDark(object? sender, RoutedEventArgs e) => SetTheme("Dark");
    private void OnThemeLight(object? sender, RoutedEventArgs e) => SetTheme("Light");

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        ApplySettings();
        _settings.Save();
        if (_vm is not null)
            _vm.StatusMessage = theme == "Light" ? "Thème clair activé" : "Thème sombre activé";
    }

    private void OnShowExtensions(object? sender, RoutedEventArgs e) =>
        _vm?.ShowPanelCommand.Execute("4");

    private void OnShowTasks(object? sender, RoutedEventArgs e) =>
        _vm?.NotAvailableCommand.Execute("développeur 5|Exécuter une tâche");
}
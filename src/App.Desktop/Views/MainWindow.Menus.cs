using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using App.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

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

    // ---------- Terminal > Exécuter une tâche... ----------

    private async void OnShowTasks(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        if (_vm.WorkspaceRoot is null)
        {
            await Choose(
                "Exécuter une tâche",
                "Ouvrez d'abord un dossier (Fichier > Ouvrir un dossier) : les tâches sont celles du dossier ouvert.",
                "OK");
            return;
        }

        IReadOnlyList<TaskDefinition> tasks = _vm.GetWorkspaceTasks();

        if (tasks.Count == 0)
        {
            int choice = await Choose(
                "Exécuter une tâche",
                "Aucune tâche trouvée dans ce dossier. Voulez-vous créer le fichier .keyce/tasks.json pour en définir ?",
                "Créer tasks.json",
                "Annuler");

            if (choice == 0)
                await _vm.OpenTasksConfigAsync();

            return;
        }

        (TaskDefinition? task, bool configure) = await PickTask(tasks);

        if (configure)
            await _vm.OpenTasksConfigAsync();
        else if (task is not null)
            await _vm.RunTaskAsync(task);
    }

    private async void OnConfigureTasks(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            await _vm.OpenTasksConfigAsync();
    }

    /// <summary>Liste des tâches : retourne la tâche choisie, ou configure = true si l'utilisateur veut éditer tasks.json.</summary>
    private async Task<(TaskDefinition? Task, bool Configure)> PickTask(IReadOnlyList<TaskDefinition> tasks)
    {
        TaskDefinition? selected = null;
        bool configure = false;

        var list = new ListBox
        {
            ItemsSource = tasks
                .Select(t => $"{t.Label}    ·    {t.CommandLine}    ({t.Source})")
                .ToList(),
            SelectedIndex = 0,
            Height = 260
        };

        var run = new Button { Content = "Exécuter" };
        var configureButton = new Button { Content = "Configurer les tâches…" };
        var cancel = new Button { Content = "Annuler" };

        var dialog = new Window
        {
            Title = "Exécuter une tâche",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Choisissez la tâche à exécuter :" },
                    list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { configureButton, cancel, run }
                    }
                }
            }
        };

        void Accept()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= tasks.Count)
                return;

            selected = tasks[list.SelectedIndex];
            dialog.Close();
        }

        run.Click += (_, _) => Accept();
        list.DoubleTapped += (_, _) => Accept();
        configureButton.Click += (_, _) =>
        {
            configure = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                Accept();
            }
            else if (args.Key == Key.Escape)
            {
                args.Handled = true;
                dialog.Close();
            }
        };

        dialog.Opened += (_, _) => list.Focus();

        await dialog.ShowDialog(this);

        return (selected, configure);
    }
}
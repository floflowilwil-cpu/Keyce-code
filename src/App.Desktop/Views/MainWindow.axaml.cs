
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using App.Core.Models;
using App.Desktop.ViewModels;
using SvcSystems.UI.Terminal;

namespace App.Desktop.Views;

public partial class MainWindow : Window
{
    private double _sidebarWidth = 260;
    private double _bottomHeight = 200;

    private readonly TerminalControlModel _terminalModel = new();
    private MainWindowViewModel? _terminalViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Initialisation de l'émulateur de terminal.
        TerminalView.Model = _terminalModel;
        _terminalModel.UserInput += OnTerminalUserInput;

        // Thème sombre au démarrage.
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Dark;

        EditorBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty &&
                DataContext is MainWindowViewModel vm)
            {
                vm.UpdateCaret(EditorBox.Text, EditorBox.CaretIndex);
            }
        };

        FindBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                UpdateFindCount();
        };

        DataContextChanged += OnDataContextChanged;
        Closed += OnWindowClosed;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    // ---------- Connexion au ViewModel ----------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Déconnexion de l'ancien ViewModel.
        if (_terminalViewModel is not null)
        {
            _terminalViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _terminalViewModel.TerminalService.OutputBytesReceived -=
                OnTerminalOutputBytesReceived;
        }

        _terminalViewModel = DataContext as MainWindowViewModel;

        // Connexion du nouveau ViewModel.
        if (_terminalViewModel is not null)
        {
            _terminalViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _terminalViewModel.TerminalService.OutputBytesReceived +=
                OnTerminalOutputBytesReceived;
        }
    }

    private void OnTerminalUserInput(
        object? sender,
        TerminalUserInputEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        // Transmet les octets tels quels au pseudo-terminal.
        _ = vm.SendTerminalInputBytesAsync(e.Data);
    }

    private void OnTerminalOutputBytesReceived(
        object? sender,
        TerminalOutputBytesEventArgs e)
    {
        var vm = _terminalViewModel;

        if (vm is null ||
            vm.ActiveTerminalSessionId != e.SessionId)
        {
            return;
        }

        // L'affichage doit être effectué sur le thread UI.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_terminalViewModel, vm) ||
                vm.ActiveTerminalSessionId != e.SessionId)
            {
                return;
            }

            _terminalModel.Feed(e.Data, e.Data.Length);
        });
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_terminalViewModel is not null)
        {
            _terminalViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _terminalViewModel.TerminalService.OutputBytesReceived -=
                OnTerminalOutputBytesReceived;
        }

        _terminalModel.UserInput -= OnTerminalUserInput;
    }

    // ---------- Thème clair / sombre ----------

    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current is not { } app)
            return;

        bool goLight = ActualThemeVariant == ThemeVariant.Dark;

        app.RequestedThemeVariant = goLight
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.StatusMessage = goLight
                ? "Thème clair activé"
                : "Thème sombre activé";

            vm.Log(vm.StatusMessage);
        }
    }

    // ---------- Affichage / masquage des panneaux ----------

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarVisible))
        {
            var cols = RootGrid.ColumnDefinitions;

            if (vm.IsSidebarVisible)
            {
                cols[1].MinWidth = 160;
                cols[1].Width = new GridLength(_sidebarWidth);
                cols[2].Width = new GridLength(4);
            }
            else
            {
                if (cols[1].ActualWidth > 0)
                    _sidebarWidth = cols[1].ActualWidth;

                cols[1].MinWidth = 0;
                cols[1].Width = new GridLength(0);
                cols[2].Width = new GridLength(0);
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsBottomVisible))
        {
            var rows = CenterGrid.RowDefinitions;

            if (vm.IsBottomVisible)
            {
                rows[2].Height = new GridLength(4);
                rows[3].MinHeight = 80;
                rows[3].Height = new GridLength(_bottomHeight);
            }
            else
            {
                if (rows[3].ActualHeight > 0)
                    _bottomHeight = rows[3].ActualHeight;

                rows[2].Height = new GridLength(0);
                rows[3].MinHeight = 0;
                rows[3].Height = new GridLength(0);
            }
        }
    }

    // ---------- Raccourcis ----------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.F)
        {
            OnFind(null, null!);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.G)
        {
            OnGoToLine(null, null!);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.P)
        {
            OnGoToFile(null, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && vm.HasFile)
        {
            FindNext(!shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsFindVisible)
        {
            vm.IsFindVisible = false;
            e.Handled = true;
        }
    }

    // ---------- Dossier / fichier ----------

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Ouvrir un dossier",
                AllowMultiple = false
            });

        if (folders.Count > 0 &&
            DataContext is MainWindowViewModel vm)
        {
            vm.OpenFolder(folders[0].Path.LocalPath);
        }
    }

    private void OnNodeSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 &&
            e.AddedItems[0] is FileNode node &&
            DataContext is MainWindowViewModel vm)
        {
            vm.OpenFile(node);
        }
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
        => EditorBox.Copy();

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        EditorBox.Focus();
        EditorBox.SelectAll();
    }

    private void OnExit(object? sender, RoutedEventArgs e)
        => Close();

    // ---------- Rechercher dans le fichier ----------

    private void OnFind(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasFile)
            return;

        vm.IsFindVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });

        UpdateFindCount();
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e)
        => FindNext(true);

    private void OnFindPrevClick(object? sender, RoutedEventArgs e)
        => FindNext(false);

    private void OnFindClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsFindVisible = false;
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void UpdateFindCount()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var q = FindBox.Text ?? "";
        var text = EditorBox.Text ?? "";

        if (q.Length == 0)
        {
            vm.FindInfo = "";
            return;
        }

        int count = 0;
        int i = 0;

        while ((i = text.IndexOf(
            q,
            i,
            StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += q.Length;
        }

        vm.FindInfo = count == 0
            ? "Aucun résultat"
            : $"{count} résultat(s)";
    }

    private void FindNext(bool forward)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var q = FindBox.Text ?? "";
        var text = EditorBox.Text ?? "";

        if (q.Length == 0 || text.Length == 0)
        {
            vm.FindInfo = "";
            return;
        }

        int selMin = Math.Min(
            EditorBox.SelectionStart,
            EditorBox.SelectionEnd);

        int selMax = Math.Max(
            EditorBox.SelectionStart,
            EditorBox.SelectionEnd);

        int idx;

        if (forward)
        {
            idx = text.IndexOf(
                q,
                Math.Min(selMax, text.Length),
                StringComparison.OrdinalIgnoreCase);

            if (idx < 0)
            {
                idx = text.IndexOf(
                    q,
                    0,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            idx = selMin > 0
                ? text.LastIndexOf(
                    q,
                    selMin - 1,
                    StringComparison.OrdinalIgnoreCase)
                : -1;

            if (idx < 0)
            {
                idx = text.LastIndexOf(
                    q,
                    text.Length - 1,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        UpdateFindCount();

        if (idx < 0)
            return;

        EditorBox.Focus();
        EditorBox.CaretIndex = idx + q.Length;
        EditorBox.SelectionStart = idx;
        EditorBox.SelectionEnd = idx + q.Length;
    }

    // ---------- Atteindre ----------

    private void OnGoToFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (!(vm.ActivePanel == 1 && vm.IsSidebarVisible))
            vm.ShowPanelCommand.Execute("1");

        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    private async void OnGoToLine(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasFile)
            return;

        var input = await Prompt(
            "Aller à la ligne",
            "Numéro de ligne :");

        if (!int.TryParse(input, out var line) || line < 1)
            return;

        var text = EditorBox.Text ?? "";

        int idx = 0;
        int current = 1;

        while (current < line)
        {
            int nl = text.IndexOf('\n', idx);

            if (nl < 0)
                break;

            idx = nl + 1;
            current++;
        }

        EditorBox.Focus();
        EditorBox.CaretIndex = idx;
        EditorBox.SelectionStart = idx;
        EditorBox.SelectionEnd = idx;
    }

    // ---------- Aide ----------

    private void OnDocumentation(object? sender, RoutedEventArgs e) =>
        ShowInfo(
            "Documentation",
            "Keyce Code : guide rapide\n\n" +
            "• Fichier > Ouvrir un dossier : affiche votre projet dans l'explorateur.\n" +
            "• Cliquez sur un fichier pour l'ouvrir dans un onglet.\n" +
            "• Barre d'activité : Explorateur, Recherche, Contrôle de code source, Exécuter, Extensions.\n" +
            "• Panneau du bas : Problèmes, Sortie, Terminal (Ctrl+J).\n" +
            "• Affichage > Thème clair / sombre : change l'apparence.\n" +
            "• Édition, Git, langages, terminal et débogage seront branchés par les modules " +
            "des développeurs 2, 3, 4 et 5.");

    private void OnShortcuts(object? sender, RoutedEventArgs e) =>
        ShowInfo(
            "Raccourcis clavier",
            "Ctrl+B : afficher / masquer la barre latérale\n" +
            "Ctrl+Maj+E : Explorateur\n" +
            "Ctrl+Maj+F : Recherche de fichiers\n" +
            "Ctrl+P : Aller au fichier\n" +
            "Ctrl+F : Rechercher dans le fichier\n" +
            "F3 / Maj+F3 : résultat suivant / précédent\n" +
            "Ctrl+G : Aller à la ligne\n" +
            "Ctrl+J : afficher / masquer le panneau du bas\n" +
            "Échap : fermer la recherche");

    private void OnAbout(object? sender, RoutedEventArgs e) =>
        ShowInfo(
            "À propos de Keyce Code",
            "Keyce Code\nVersion 0.1 (projet universitaire)\n\n" +
            "Éditeur de code de bureau développé en C# / .NET avec Avalonia UI.\n" +
            "Module interface graphique : développeur 1.",
            true);

    private async void ShowInfo(
        string title,
        string body,
        bool showLogo = false)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12
        };

        if (showLogo)
        {
            panel.Children.Add(new Image
            {
                Source = new Bitmap(
                    AssetLoader.Open(
                        new Uri("avares://App.Desktop/Assets/logo.png"))),
                Width = 96,
                Height = 96
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap
        });

        var close = new Button
        {
            Content = "Fermer",
            HorizontalAlignment = HorizontalAlignment.Right
        };

        panel.Children.Add(close);

        var dlg = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        close.Click += (_, _) => dlg.Close();

        await dlg.ShowDialog(this);
    }

    private async Task<string?> Prompt(string title, string label)
    {
        var box = new TextBox { MinWidth = 200 };

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dlg = new Window
        {
            Title = title,
            Width = 320,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    ok
                }
            }
        };

        string? result = null;

        ok.Click += (_, _) =>
        {
            result = box.Text;
            dlg.Close();
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                dlg.Close();
            }
        };

        dlg.Opened += (_, _) => box.Focus();

        await dlg.ShowDialog(this);

        return result;
    }
}


using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    private MainWindowViewModel? _vm;

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

        // Le TextBox est un miroir : on intercepte la frappe AVANT lui et on la renvoie au moteur.
        EditorBox.AddHandler(TextInputEvent, OnEditorTextInput, RoutingStrategies.Tunnel);
        EditorBox.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) DetachViewModel(_vm);
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null) AttachViewModel(_vm);
        };

        Activated += async (_, _) =>
        {
            if (_vm is not null) await _vm.CheckExternalChangesAsync();
        };

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void AttachViewModel(MainWindowViewModel vm)
    {
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.SelectionRequested += OnSelectionRequested;
        vm.ConflictDetected += OnConflictDetected;
    }

    private void DetachViewModel(MainWindowViewModel vm)
    {
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.SelectionRequested -= OnSelectionRequested;
        vm.ConflictDetected -= OnConflictDetected;
    }

    // ---------- Le ViewModel demande de placer la sélection ----------
    private void OnSelectionRequested(int anchor, int active)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int len = EditorBox.Text?.Length ?? 0;
            anchor = Math.Clamp(anchor, 0, len);
            active = Math.Clamp(active, 0, len);
            EditorBox.CaretIndex = active;
            EditorBox.SelectionStart = anchor;
            EditorBox.SelectionEnd = active;
        });
    }

    private void SyncSelection() =>
        _vm?.SyncSelection(EditorBox.SelectionStart, EditorBox.SelectionEnd, EditorBox.CaretIndex);

    // ---------- Frappe dans l'éditeur → IEditorCommands ----------
    private void OnEditorTextInput(object? sender, TextInputEventArgs e)
    {
        if (_vm is null || !_vm.HasFile || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text[0] < ' ') return;   // Entrée / Tab sont traitées dans OnEditorKeyDown

        SyncSelection();
        string text = e.Text;
        _vm.ExecuteEdit((c, id) => c.TypeText(id, text));
        e.Handled = true;
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || !_vm.HasFile) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when !shift: _vm.UndoCommand.Execute(null); e.Handled = true; return;
                case Key.Y:
                case Key.Z when shift: _vm.RedoCommand.Execute(null); e.Handled = true; return;
                case Key.C: e.Handled = true; await CopyAsync(cut: false); return;
                case Key.X: e.Handled = true; await CopyAsync(cut: true); return;
                case Key.V: e.Handled = true; await PasteAsync(); return;
                case Key.D when shift:
                    SyncSelection(); _vm.ExecuteEdit((c, id) => c.DuplicateLines(id)); e.Handled = true; return;
                case Key.Oem2 when !shift:   // Ctrl+/
                    SyncSelection(); _vm.ExecuteEdit((c, id) => c.CommentLines(id)); e.Handled = true; return;
                case Key.Oem2 when shift:
                    SyncSelection(); _vm.ExecuteEdit((c, id) => c.UncommentLines(id)); e.Handled = true; return;
            }
            return;   // Ctrl+A, Ctrl+Home… restent gérés par le TextBox (sélection / navigation)
        }

        if (alt && e.Key is Key.Up or Key.Down)
        {
            SyncSelection();
            if (e.Key == Key.Up) _vm.ExecuteEdit((c, id) => c.MoveLinesUp(id));
            else _vm.ExecuteEdit((c, id) => c.MoveLinesDown(id));
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Back:
                SyncSelection(); _vm.ExecuteEdit((c, id) => c.Backspace(id)); e.Handled = true; break;
            case Key.Delete:
                SyncSelection(); _vm.ExecuteEdit((c, id) => c.DeleteForward(id)); e.Handled = true; break;
            case Key.Enter:
                SyncSelection(); _vm.ExecuteEdit((c, id) => c.InsertNewLine(id)); e.Handled = true; break;
            case Key.Tab:
                SyncSelection();
                if (shift) _vm.ExecuteEdit((c, id) => c.Outdent(id));
                else _vm.ExecuteEdit((c, id) => c.Indent(id));
                e.Handled = true;
                break;
        }
    }

    // ---------- Presse-papiers (le texte vient du moteur : copier sans sélection = la ligne) ----------
    private async Task CopyAsync(bool cut)
    {
        if (_vm is null) return;
        SyncSelection();
        string? text = cut ? _vm.CutText() : _vm.CopyText();
        if (text is not null && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (_vm is null || Clipboard is not { } clipboard) return;
        using var data = await clipboard.TryGetDataAsync();
        string? text = data is null ? null : await data.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        SyncSelection();
        _vm.ExecuteEdit((c, id) => c.Paste(id, text));
    }

    private async void OnCut(object? sender, RoutedEventArgs e) => await CopyAsync(cut: true);
    private async void OnCopy(object? sender, RoutedEventArgs e) => await CopyAsync(cut: false);
    private async void OnPaste(object? sender, RoutedEventArgs e) => await PasteAsync();

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
        else if (e.PropertyName == nameof(MainWindowViewModel.TerminalOutput))
        {
            // Suit la fin de la sortie du terminal.
            Dispatcher.UIThread.Post(() => TerminalBox.CaretIndex = TerminalBox.Text?.Length ?? 0);
        }
    }

    // ---------- Raccourcis globaux ----------
    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.F) { OnFind(null, null!); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { OnFind(null, null!); e.Handled = true; }
        else if (ctrl && e.Key == Key.G) { OnGoToLine(null, null!); e.Handled = true; }
        else if (ctrl && e.Key == Key.P) { OnGoToFile(null, null!); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { vm.NewFileCommand.Execute(null); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { e.Handled = true; await SaveAsAsync(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
        else if (e.Key == Key.F3 && vm.HasFile) { SyncSelection(); vm.FindNext(!shift); e.Handled = true; }
        else if (e.Key == Key.Escape && vm.IsFindVisible) { vm.IsFindVisible = false; e.Handled = true; }
    }

    // ---------- Dossier / fichiers ----------
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
            Title = "Ouvrir un dossier",
            AllowMultiple = false
        });
        if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
            await vm.OpenWorkspaceAsync(folders[0].Path.LocalPath);
    }

    private async void OnNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is FileNode node
            && DataContext is MainWindowViewModel vm)
            await vm.OpenFileAsync(node);
    }

    private async void OnProblemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ProblemItem problem
            && DataContext is MainWindowViewModel vm)
        {
            await vm.OpenProblemAsync(problem);
            if (sender is ListBox lb) lb.SelectedItem = null;
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e)
        => Close();

    // ---------- Enregistrer / fermer ----------
    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveAsync();
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    /// <summary>Enregistre le document actif (« Enregistrer sous » s'il n'a pas encore de fichier).</summary>
    private Task<bool> SaveAsync() => SaveTabAsync(_vm?.ActiveTab);

    private async Task<bool> SaveTabAsync(DocumentTab? tab)
    {
        if (_vm is null || tab is null) return false;
        return tab.Path is null ? await SaveTabAsAsync(tab) : await _vm.SaveAsync(tab);
    }

    private Task<bool> SaveAsAsync() => SaveTabAsAsync(_vm?.ActiveTab);

    private async Task<bool> SaveTabAsAsync(DocumentTab? tab)
    {
        if (_vm is null || tab is null) return false;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Enregistrer sous",
            SuggestedFileName = tab.Title
        });
        if (file is null) return false;
        return await _vm.SaveAsAsync(tab, file.Path.LocalPath);
    }

    private async void OnCloseActive(object? sender, RoutedEventArgs e) =>
        await CloseTabWithConfirmationAsync(_vm?.ActiveTab);

    private async Task CloseTabWithConfirmationAsync(DocumentTab? tab)
    {
        if (_vm is null || tab is null) return;
        if (tab.IsDirty)
        {
            int choice = await Choose("Enregistrer les modifications ?",
                $"« {tab.Title} » contient des modifications non enregistrées.",
                "Enregistrer", "Ne pas enregistrer", "Annuler");
            if (choice == 0)
            {
                if (!await SaveTabAsync(tab)) return;
            }
            else if (choice != 1) return;
        }
        _vm.CloseTab(tab);
    }

    // ---------- Onglets ----------
    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentTab tab } && _vm is not null)
            _vm.ActiveTab = tab;
    }

    private async void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: DocumentTab tab })
            await CloseTabWithConfirmationAsync(tab);
    }

    // ---------- Conflit : le fichier a changé sur le disque ET le document a des modifs locales ----------
    private async void OnConflictDetected(object? sender, ExternalChangeEventArgs e)
    {
        int choice = await Choose("Fichier modifié sur le disque",
            $"« {Path.GetFileName(e.Path)} » a changé sur le disque alors qu'il contient des modifications non enregistrées.",
            "Recharger depuis le disque", "Garder ma version");
        if (_vm is not null && choice >= 0)
            await _vm.ResolveConflictAsync(e.Id, reloadFromDisk: choice == 0);
    }

    // ---------- Rechercher / remplacer ----------
    private void OnSelectAll(object? sender, RoutedEventArgs e) => _vm?.SelectAllTextCommand.Execute(null);

    private void OnFind(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasFile)
            return;

        vm.IsFindVisible = true;
        Dispatcher.UIThread.Post(() => { FindBox.Focus(); FindBox.SelectAll(); });
        vm.UpdateFindCount();
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e) { SyncSelection(); _vm?.FindNext(true); }
    private void OnFindPrevClick(object? sender, RoutedEventArgs e) { SyncSelection(); _vm?.FindNext(false); }

    private void OnFindClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsFindVisible = false;
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SyncSelection();
            _vm?.FindNext(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnReplaceClick(null, null!); e.Handled = true; }
    }

    private void OnReplaceClick(object? sender, RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.ReplaceNextCommand.Execute(null);
    }

    private void OnReplaceAllClick(object? sender, RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.ReplaceAllCommand.Execute(null);
    }

    // ---------- Terminal ----------
    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.SendTerminalInputCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------- Git ----------
    private void OnGitStageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitFileStatus file } && _vm is not null)
            _vm.Git.StageCommand.Execute(file.Path);
    }

    private void OnGitUnstageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitFileStatus file } && _vm is not null)
            _vm.Git.UnstageCommand.Execute(file.Path);
    }

    /// <summary>Clic sur un fichier du panneau Git : affiche son diff dans l'onglet Sortie.</summary>
    private async void OnGitFilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GitFileStatus file } || _vm is null) return;
        await _vm.Git.LoadDiffCommand.ExecuteAsync(file.Path);
        _vm.Log($"Diff de {file.Path} :\n{_vm.Git.Controller.CurrentDiff}");
        _vm.ShowOutput();
    }

    // ---------- Débogage ----------
    private async void OnBrowseProgram(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir le programme .NET à déboguer",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Assembly .NET") { Patterns = ["*.dll"] }]
        });
        if (files.Count > 0) _vm.DebugProgram = files[0].Path.LocalPath;
    }

    private void OnAddBreakpoint(object? sender, RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.AddBreakpointAtCaret();
    }

    private void OnStartDebug(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!(_vm.ActivePanel == 3 && _vm.IsSidebarVisible)) _vm.ShowPanelCommand.Execute("3");
        _vm.StartDebugCommand.Execute(null);
    }

    // ---------- Extensions ----------
    private async void OnInstallExtension(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Dossier de l'extension (contenant extension.json)",
            AllowMultiple = false
        });
        if (folders.Count > 0) await _vm.InstallExtensionAsync(folders[0].Path.LocalPath);
    }

    private void OnExtensionToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } && _vm is not null)
            _vm.ToggleExtensionCommand.Execute(item.Id);
    }

    private void OnExtensionUninstall(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } && _vm is not null)
            _vm.UninstallExtensionCommand.Execute(item.Id);
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
        if (DataContext is not MainWindowViewModel vm || !vm.HasFile) return;
        var input = await Prompt("Aller à la ligne", "Numéro de ligne :");
        if (!int.TryParse(input, out var line) || line < 1) return;
        vm.GoToLine(line);
        EditorBox.Focus();
    }

    // ---------- Aide ----------

    private void OnDocumentation(object? sender, RoutedEventArgs e) =>
        ShowInfo(
            "Documentation",
            "Keyce Code : guide rapide\n\n" +
            "• Fichier > Ouvrir un dossier : affiche votre projet dans l'explorateur.\n" +
            "• Cliquez sur un fichier pour l'ouvrir dans un onglet ; Ctrl+S enregistre.\n" +
            "• Barre d'activité : Explorateur, Recherche, Contrôle de code source, Exécuter, Extensions.\n" +
            "• Panneau du bas : Problèmes (diagnostics LSP), Sortie, Terminal (Ctrl+J).\n" +
            "• Affichage > Thème clair / sombre : change l'apparence.");

    private void OnShortcuts(object? sender, RoutedEventArgs e) =>
        ShowInfo("Raccourcis clavier",
            "Ctrl+N : nouveau fichier\n" +
            "Ctrl+S / Ctrl+Maj+S : enregistrer / enregistrer sous\n" +
            "Ctrl+Z / Ctrl+Y : annuler / rétablir\n" +
            "Ctrl+X / Ctrl+C / Ctrl+V : couper / copier / coller\n" +
            "Ctrl+Maj+D : dupliquer la ligne\n" +
            "Alt+↑ / Alt+↓ : déplacer la ligne\n" +
            "Ctrl+/ : commenter (Ctrl+Maj+/ : décommenter)\n" +
            "Ctrl+B : afficher / masquer la barre latérale\n" +
            "Ctrl+Maj+E : Explorateur\n" +
            "Ctrl+Maj+F : Recherche de fichiers\n" +
            "Ctrl+P : Aller au fichier\n" +
            "Ctrl+F / Ctrl+H : rechercher / remplacer dans le fichier\n" +
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

    /// <summary>Boîte de dialogue à N boutons. Retourne l'indice du bouton cliqué, ou -1 si fermée.</summary>
    private async Task<int> Choose(string title, string message, params string[] buttons)
    {
        int result = -1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var dlg = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, row }
            }
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i] };
            b.Click += (_, _) => { result = index; dlg.Close(); };
            row.Children.Add(b);
        }
        await dlg.ShowDialog(this);
        return result;
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

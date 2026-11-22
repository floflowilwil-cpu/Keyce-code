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
    private double _sidebarWidth = 240;
    private double _rightPanelWidth = 350;
    private double _bottomHeight = 200;
    private MainWindowViewModel? _vm;

    private readonly TerminalControlModel _terminalModel = new();

    public MainWindow()
    {
        InitializeComponent();

        // Initialisation de l'émulateur de terminal.
        TerminalView.Model = _terminalModel;
        _terminalModel.UserInput += OnTerminalUserInput;

        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = ThemeVariant.Dark;

        DataContextChanged += OnDataContextChanged;
        Activated += OnActivated;
        Closed += OnWindowClosed;

        AddHandler(
            KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel);
    }

    // ================================================================
    // Connexion au ViewModel
    // ================================================================

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            DetachViewModel(_vm);

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
            AttachViewModel(_vm);
    }

    private void AttachViewModel(MainWindowViewModel vm)
    {
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.ConflictDetected += OnConflictDetected;

        CodeEditor.Attach(vm.EditorService, vm.EditorCommands);
        CodeEditor.DocumentId = vm.ActiveTab?.Id;
        CodeEditor.CaretChanged += OnEditorCaretChanged;
        CodeEditor.SaveRequested += OnEditorSaveRequested;
        CodeEditor.EditRejected += OnEditorEditRejected;

        vm.TerminalService.OutputBytesReceived +=
            OnTerminalOutputBytesReceived;

        ApplyRightPanelVisibility(vm);
        ApplyRightPanelTab(vm);
    }

    private void DetachViewModel(MainWindowViewModel vm)
    {
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.ConflictDetected -= OnConflictDetected;

        CodeEditor.CaretChanged -= OnEditorCaretChanged;
        CodeEditor.SaveRequested -= OnEditorSaveRequested;
        CodeEditor.EditRejected -= OnEditorEditRejected;

        vm.TerminalService.OutputBytesReceived -=
            OnTerminalOutputBytesReceived;
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        if (_vm is not null)
            await _vm.CheckExternalChangesAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_vm is not null)
            DetachViewModel(_vm);

        _terminalModel.UserInput -= OnTerminalUserInput;
    }

    // ================================================================
    // Terminal PTY
    // ================================================================

    private void OnTerminalUserInput(
        object? sender,
        TerminalUserInputEventArgs e)
    {
        if (_vm is null)
            return;

        // Transmet directement les octets au pseudo-terminal.
        _ = _vm.SendTerminalInputBytesAsync(e.Data);
    }

    private void OnTerminalOutputBytesReceived(
        object? sender,
        TerminalOutputBytesEventArgs e)
    {
        var vm = _vm;

        if (vm is null ||
            vm.ActiveTerminalSessionId != e.SessionId)
        {
            return;
        }

        // L'émulateur graphique doit être mis à jour sur le thread UI.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_vm, vm) ||
                vm.ActiveTerminalSessionId != e.SessionId)
            {
                return;
            }

            _terminalModel.Feed(e.Data, e.Data.Length);
        });
    }

    // ================================================================
    // Éditeur
    // ================================================================

    private void OnEditorCaretChanged(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var caret = CodeEditor.CaretPosition;
        _vm.Position = $"Ln {caret.Line + 1}, Col {caret.Column + 1}";
    }

    private async void OnEditorSaveRequested(object? sender, EventArgs e)
    {
        await SaveAsync();
    }

    private void OnEditorEditRejected(object? sender, string message)
    {
        if (_vm is not null) _vm.StatusMessage = message;
    }

    // Le contrôle conserve directement la sélection dans IEditorService.
    // Cette méthode reste appelée par les commandes de recherche et les menus.
    private void SyncSelection() { }

    private async void OnCut(
        object? sender,
        RoutedEventArgs e)
    {
        await CodeEditor.CopyAsync(cut: true);
    }

    private async void OnCopy(
        object? sender,
        RoutedEventArgs e)
    {
        await CodeEditor.CopyAsync(cut: false);
    }

    private async void OnPaste(
        object? sender,
        RoutedEventArgs e)
    {
        await CodeEditor.PasteAsync();
    }

    // ================================================================
    // Thème
    // ================================================================

    private void OnToggleTheme(
        object? sender,
        RoutedEventArgs e)
    {
        if (Avalonia.Application.Current is not { } app)
            return;

        bool goLight = ActualThemeVariant == ThemeVariant.Dark;

        app.RequestedThemeVariant = goLight
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        if (_vm is not null)
        {
            _vm.StatusMessage = goLight
                ? "Thème clair activé"
                : "Thème sombre activé";

            _vm.Log(_vm.StatusMessage);
        }
    }

    // ================================================================
    // Affichage des panneaux
    // ================================================================

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.ActiveTab))
            CodeEditor.DocumentId = vm.ActiveTab?.Id;

        if (e.PropertyName ==
            nameof(MainWindowViewModel.IsSidebarVisible))
        {
            var columns = RootGrid.ColumnDefinitions;

            if (vm.IsSidebarVisible)
            {
                columns[1].MinWidth = 160;
                columns[1].Width = new GridLength(_sidebarWidth);
                columns[2].Width = new GridLength(4);
            }
            else
            {
                if (columns[1].ActualWidth > 0)
                    _sidebarWidth = columns[1].ActualWidth;

                columns[1].MinWidth = 0;
                columns[1].Width = new GridLength(0);
                columns[2].Width = new GridLength(0);
            }
        }
        else if (e.PropertyName ==
                 nameof(MainWindowViewModel.IsBottomVisible))
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
        else if (e.PropertyName ==
                 nameof(MainWindowViewModel.IsRightPanelVisible))
        {
            ApplyRightPanelVisibility(vm);
        }
        else if (e.PropertyName ==
                 nameof(MainWindowViewModel.RightPanelTab))
        {
            ApplyRightPanelTab(vm);
        }
    }

    private void ApplyRightPanelVisibility(MainWindowViewModel vm)
    {
        var columns = RootGrid.ColumnDefinitions;
        if (columns.Count < 6)
            return;

        if (vm.IsRightPanelVisible)
        {
            columns[4].Width = new GridLength(4);
            columns[5].MinWidth = 240;
            columns[5].Width = new GridLength(_rightPanelWidth);
        }
        else
        {
            if (columns[5].ActualWidth > 0)
                _rightPanelWidth = columns[5].ActualWidth;

            columns[4].Width = new GridLength(0);
            columns[5].MinWidth = 0;
            columns[5].Width = new GridLength(0);
        }
    }

    private void ApplyRightPanelTab(MainWindowViewModel vm)
    {
        var rows = RightContentGrid.RowDefinitions;
        if (rows.Count < 3)
            return;

        switch (vm.RightPanelTab)
        {
            case 1:
                rows[0].Height = new GridLength(1, GridUnitType.Star);
                rows[1].Height = new GridLength(0);
                rows[2].Height = new GridLength(0);
                break;
            case 2:
                rows[0].Height = new GridLength(0);
                rows[1].Height = new GridLength(0);
                rows[2].Height = new GridLength(1, GridUnitType.Star);
                break;
            default:
                rows[0].Height = new GridLength(43, GridUnitType.Star);
                rows[1].Height = new GridLength(4);
                rows[2].Height = new GridLength(57, GridUnitType.Star);
                break;
        }
    }

    // ================================================================
    // Raccourcis clavier
    // ================================================================

    private async void OnWindowKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (_vm is null)
            return;

        bool ctrl =
            e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        bool shift =
            e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.F)
        {
            OnFind(null, null!);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.H)
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
        else if (ctrl && e.Key == Key.O)
        {
            e.Handled = true;
            OnOpenFile(null, null!);
        }
        else if (ctrl && e.Key == Key.N)
        {
            _vm.NewFileCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveAsAsync();
        }
        else if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            await SaveAsync();
        }
        else if (e.Key == Key.F3 && _vm.HasFile)
        {
            SyncSelection();
            _vm.FindNext(!shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsFindVisible)
        {
            _vm.IsFindVisible = false;
            e.Handled = true;
        }
    }

    // ================================================================
    // Dossiers et fichiers
    // ================================================================

    private async void OnOpenFolder(
        object? sender,
        RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Ouvrir un dossier",
                AllowMultiple = false
            });

        if (folders.Count > 0 && _vm is not null)
        {
            await _vm.OpenWorkspaceAsync(
                folders[0].Path.LocalPath);
        }
    }

    private async void OnNodeSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 &&
            e.AddedItems[0] is FileNode node &&
            _vm is not null)
        {
            _vm.SelectedNode = node;
            await _vm.OpenFileAsync(node);
        }
    }

    // ================================================================
    // Fichier : ouvrir un fichier, créer un dossier / fichier, supprimer
    // ================================================================

    private async void OnOpenFile(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Ouvrir un fichier",
                AllowMultiple = true
            });

        foreach (var file in files)
            await _vm.OpenPathAsync(file.Path.LocalPath);
    }

    private async void OnNewFileHere(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        // Sans dossier ouvert : fichier sans titre (« Enregistrer sous » demandera l'emplacement).
        if (!_vm.HasFolder)
        {
            _vm.NewFileCommand.Execute(null);
            return;
        }

        string? name = await Prompt("Nouveau fichier", "Nom du fichier :");

        if (!string.IsNullOrWhiteSpace(name))
            await _vm.CreateEntryAsync(name, isDirectory: false);
    }

    private async void OnNewFolderHere(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        if (_vm.HasFolder)
        {
            string? name = await Prompt("Nouveau dossier", "Nom du dossier :");

            if (!string.IsNullOrWhiteSpace(name))
                await _vm.CreateEntryAsync(name, isDirectory: true);

            return;
        }

        // Aucun dossier ouvert : on choisit où le créer, puis on l'ouvre comme espace de travail.
        var parents = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Où créer le nouveau dossier ?",
                AllowMultiple = false
            });

        if (parents.Count == 0)
            return;

        string? folderName = await Prompt("Nouveau dossier", "Nom du dossier :");

        if (string.IsNullOrWhiteSpace(folderName))
            return;

        string? created = await _vm.CreateEntryAsync(
            folderName,
            isDirectory: true,
            parentFolder: parents[0].Path.LocalPath);

        if (created is not null)
            await _vm.OpenWorkspaceAsync(created);
    }

    private async void OnDeleteSelected(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node)
            return;

        int choice = await Choose(
            "Supprimer",
            node.IsDirectory
                ? $"Supprimer le dossier « {node.Name} » et tout son contenu ? Cette action est définitive."
                : $"Supprimer le fichier « {node.Name} » ? Cette action est définitive.",
            "Supprimer",
            "Annuler");

        if (choice == 0)
            await _vm.DeleteNodeAsync(node);
    }

    private void OnRefreshExplorer(
        object? sender,
        RoutedEventArgs e) =>
        _vm?.RefreshExplorerCommand.Execute(null);

    private async void OnProblemSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 &&
            e.AddedItems[0] is ProblemItem problem &&
            _vm is not null)
        {
            await _vm.OpenProblemAsync(problem);

            if (sender is ListBox listBox)
                listBox.SelectedItem = null;
        }
    }

    private void OnExit(
        object? sender,
        RoutedEventArgs e)
    {
        Close();
    }

    // ================================================================
    // Enregistrement
    // ================================================================

    private async void OnSave(
        object? sender,
        RoutedEventArgs e)
    {
        await SaveAsync();
    }

    private async void OnSaveAs(
        object? sender,
        RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private Task<bool> SaveAsync()
    {
        return SaveTabAsync(_vm?.ActiveTab);
    }

    private async Task<bool> SaveTabAsync(DocumentTab? tab)
    {
        if (_vm is null || tab is null)
            return false;

        return tab.Path is null
            ? await SaveTabAsAsync(tab)
            : await _vm.SaveAsync(tab);
    }

    private Task<bool> SaveAsAsync()
    {
        return SaveTabAsAsync(_vm?.ActiveTab);
    }

    private async Task<bool> SaveTabAsAsync(DocumentTab? tab)
    {
        if (_vm is null || tab is null)
            return false;

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Enregistrer sous",
                SuggestedFileName = tab.Title
            });

        if (file is null)
            return false;

        return await _vm.SaveAsAsync(
            tab,
            file.Path.LocalPath);
    }

    // ================================================================
    // Onglets
    // ================================================================

    private async void OnCloseActive(
        object? sender,
        RoutedEventArgs e)
    {
        await CloseTabWithConfirmationAsync(_vm?.ActiveTab);
    }

    private async Task CloseTabWithConfirmationAsync(
        DocumentTab? tab)
    {
        if (_vm is null || tab is null)
            return;

        if (tab.IsDirty)
        {
            int choice = await Choose(
                "Enregistrer les modifications ?",
                $"« {tab.Title} » contient des modifications non enregistrées.",
                "Enregistrer",
                "Ne pas enregistrer",
                "Annuler");

            if (choice == 0)
            {
                if (!await SaveTabAsync(tab))
                    return;
            }
            else if (choice != 1)
            {
                return;
            }
        }

        _vm.CloseTab(tab);
    }

    private void OnTabPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentTab tab } &&
            _vm is not null)
        {
            _vm.ActiveTab = tab;
        }
    }

    private async void OnTabCloseClick(
        object? sender,
        RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Control { DataContext: DocumentTab tab })
            await CloseTabWithConfirmationAsync(tab);
    }

    // ================================================================
    // Conflits de fichiers externes
    // ================================================================

    private async void OnConflictDetected(
        object? sender,
        ExternalChangeEventArgs e)
    {
        int choice = await Choose(
            "Fichier modifié sur le disque",
            $"« {Path.GetFileName(e.Path)} » a changé sur le disque alors qu'il contient des modifications non enregistrées.",
            "Recharger depuis le disque",
            "Garder ma version");

        if (_vm is not null && choice >= 0)
        {
            await _vm.ResolveConflictAsync(
                e.Id,
                reloadFromDisk: choice == 0);
        }
    }

    // ================================================================
    // Sélection et recherche
    // ================================================================

    private void OnSelectAll(
        object? sender,
        RoutedEventArgs e)
    {
        _vm?.SelectAllTextCommand.Execute(null);
    }

    private void OnFind(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null || !_vm.HasFile)
            return;

        _vm.IsFindVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });

        _vm.UpdateFindCount();
    }

    private void OnFindNextClick(
        object? sender,
        RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.FindNext(true);
    }

    private void OnFindPrevClick(
        object? sender,
        RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.FindNext(false);
    }

    private void OnFindClose(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.IsFindVisible = false;
    }

    private void OnFindKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SyncSelection();
            _vm?.FindNext(
                !e.KeyModifiers.HasFlag(KeyModifiers.Shift));

            e.Handled = true;
        }
    }

    private void OnReplaceKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnReplaceClick(null, null!);
            e.Handled = true;
        }
    }

    private void OnReplaceClick(
        object? sender,
        RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.ReplaceNextCommand.Execute(null);
    }

    private void OnReplaceAllClick(
        object? sender,
        RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.ReplaceAllCommand.Execute(null);
    }

    // ================================================================
    // Git
    // ================================================================

    private void OnGitStageClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitFileStatus file } &&
            _vm is not null)
        {
            _vm.Git.StageCommand.Execute(file.Path);
        }
    }

    private void OnGitUnstageClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitFileStatus file } &&
            _vm is not null)
        {
            _vm.Git.UnstageCommand.Execute(file.Path);
        }
    }

    private async void OnGitFilePressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GitFileStatus file } ||
            _vm is null)
        {
            return;
        }

        await _vm.Git.LoadDiffCommand.ExecuteAsync(file.Path);

        _vm.Log(
            $"Diff de {file.Path} :\n{_vm.Git.Controller.CurrentDiff}");

        _vm.ShowOutput();
    }

    private void OnGitCheckoutBranchClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitBranch branch } &&
            _vm is not null)
        {
            _vm.Git.CheckoutBranchCommand.Execute(branch.Name);
        }
    }

    private void OnGitMergeBranchClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitBranch branch } &&
            _vm is not null)
        {
            _vm.Git.MergeBranchCommand.Execute(branch.Name);
        }
    }

    // ================================================================
    // Débogage
    // ================================================================

    private async void OnBrowseProgram(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Choisir le programme .NET à déboguer",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Assembly .NET")
                    {
                        Patterns = ["*.dll"]
                    }
                ]
            });

        if (files.Count > 0)
            _vm.DebugProgram = files[0].Path.LocalPath;
    }

    private void OnAddBreakpoint(
        object? sender,
        RoutedEventArgs e)
    {
        SyncSelection();
        _vm?.AddBreakpointAtCaret();
    }

    private void OnStartDebug(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        if (!(_vm.ActivePanel == 3 && _vm.IsSidebarVisible))
            _vm.ShowPanelCommand.Execute("3");

        _vm.StartDebugCommand.Execute(null);
    }

    // ================================================================
    // Extensions
    // ================================================================

    private async void OnInstallExtension(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Dossier de l'extension (contenant extension.json)",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            await _vm.InstallExtensionAsync(
                folders[0].Path.LocalPath);
        }
    }

    private void OnExtensionToggle(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } &&
            _vm is not null)
        {
            _vm.ToggleExtensionCommand.Execute(item.Id);
        }
    }

    private void OnExtensionOpenReadme(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } &&
            _vm is not null)
        {
            _vm.OpenExtensionReadmeCommand.Execute(item.Id);
        }
    }

    private void OnExtensionUninstall(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } &&
            _vm is not null)
        {
            _vm.UninstallExtensionCommand.Execute(item.Id);
        }
    }

    private async void OnUpdateExtension(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Dossier de la nouvelle version (extension.json)",
                AllowMultiple = false
            });

        if (folders.Count > 0)
        {
            await _vm.UpdateExtensionAsync(
                folders[0].Path.LocalPath);
        }
    }

    private void OnExecuteExtensionCommand(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CommandInfo cmd } &&
            _vm is not null)
        {
            _vm.ExecuteExtensionCommandCommand.Execute(cmd.Id);
        }
    }

    private void OnExtensionUpdateFromCatalog(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExtensionItem item } &&
            _vm is not null)
        {
            _vm.UpdateFromCatalogCommand.Execute(item.Id);
        }
    }

    private void OnCatalogInstallClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CatalogExtensionItem item } &&
            _vm is not null)
        {
            _vm.InstallFromCatalogCommand.Execute(item.Id);
        }
    }

    private void OnCatalogUpdateClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CatalogExtensionItem item } &&
            _vm is not null)
        {
            _vm.UpdateFromCatalogCommand.Execute(item.Id);
        }
    }

    // ================================================================
    // Navigation
    // ================================================================

    private void OnGoToFile(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        if (!(_vm.ActivePanel == 1 && _vm.IsSidebarVisible))
            _vm.ShowPanelCommand.Execute("1");

        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    private async void OnGoToLine(
        object? sender,
        RoutedEventArgs e)
    {
        if (_vm is null || !_vm.HasFile)
            return;

        var input = await Prompt(
            "Aller à la ligne",
            "Numéro de ligne :");

        if (!int.TryParse(input, out int line) || line < 1)
            return;

        _vm.GoToLine(line);
        CodeEditor.Focus();
    }

    // ================================================================
    // Aide
    // ================================================================

    private void OnDocumentation(
        object? sender,
        RoutedEventArgs e)
    {
        ShowInfo(
            "Documentation",
            "Keyce Code : guide rapide\n\n" +
            "• Fichier > Ouvrir un dossier : affiche votre projet dans l'explorateur.\n" +
            "• Cliquez sur un fichier pour l'ouvrir dans un onglet ; Ctrl+S enregistre.\n" +
            "• Barre d'activité : Explorateur, Recherche, Contrôle de code source, Exécuter, Extensions.\n" +
            "• Panneau du bas : Problèmes, Sortie, Terminal (Ctrl+J).\n" +
            "• Affichage > Thème clair / sombre : change l'apparence.");
    }

    private void OnShortcuts(
        object? sender,
        RoutedEventArgs e)
    {
        ShowInfo(
            "Raccourcis clavier",
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
    }

    private void OnAbout(
        object? sender,
        RoutedEventArgs e)
    {
        ShowInfo(
            "À propos de Keyce Code",
            "Keyce Code\nVersion 0.1 (projet universitaire)\n\n" +
            "Éditeur de code de bureau développé en C# / .NET avec Avalonia UI.\n" +
            "Module interface graphique : développeur 1.",
            true);
    }

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

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        close.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }

    // ================================================================
    // Boîtes de dialogue
    // ================================================================

    private async Task<int> Choose(
        string title,
        string message,
        params string[] buttons)
    {
        int result = -1;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dialog = new Window
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
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap
                    },
                    row
                }
            }
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;

            var button = new Button
            {
                Content = buttons[i]
            };

            button.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };

            row.Children.Add(button);
        }

        await dialog.ShowDialog(this);

        return result;
    }

    private async Task<string?> Prompt(
        string title,
        string label)
    {
        var box = new TextBox
        {
            MinWidth = 200
        };

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dialog = new Window
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
            dialog.Close();
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = box.Text;
                dialog.Close();
            }
        };

        dialog.Opened += (_, _) => box.Focus();

        await dialog.ShowDialog(this);

        return result;
    }
}

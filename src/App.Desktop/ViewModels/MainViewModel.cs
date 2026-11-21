using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using App.Application.Composition;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.Desktop.ViewModels;

public class FileNode
{
    public string Name { get; }
    public string FullPath { get; }
    public string Directory { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    public FileNode(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        Directory = Path.GetDirectoryName(fullPath) ?? "";
        IsDirectory = isDirectory;
    }
}

/// <summary>
/// Ligne de l'onglet Problèmes, alimentée par ILanguageClient.DiagnosticsPublished.
/// </summary>
public sealed record ProblemItem(
    string Path,
    string FileName,
    int Line,
    int Column,
    string Severity,
    string Message)
{
    public string Location => $"{FileName} [{Line}, {Column}]";
}

/// <summary>
/// ViewModel de la fenêtre principale.
/// Les services passent par les contrats de App.Core via AppServices.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private static readonly HashSet<string> Ignored =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules"
        };

    private readonly AppServices _s;
    private TerminalSessionId? _terminalSessionId;

    public MainWindowViewModel(AppServices services)
    {
        _s = services;
        Git = new GitPanelViewModel(services.Git);

        _s.Workspace.WorkspaceChanged += (_, e) =>
            UI(() => ApplyWorkspace(e.Current));

        _s.Editor.DocumentOpened += (_, i) =>
            UI(() => AddTab(i));

        _s.Editor.DocumentClosed += (_, i) =>
            UI(() => RemoveTab(i.Id));

        _s.Editor.DocumentStateChanged += (_, i) =>
            UI(() => OnDocumentState(i));

        _s.Editor.DocumentChanged += (_, e) =>
            UI(() => OnDocumentChanged(e.Id));

        _s.Editor.ExternalChange += (_, e) =>
            UI(() => OnExternalChange(e));

        _s.Languages.DiagnosticsPublished += (_, e) =>
            UI(() => OnDiagnostics(e));

        _s.LanguageMessage += (_, m) =>
            UI(() => Log(m));

        WirePanels();
    }

    private static void UI(Action action) =>
        Dispatcher.UIThread.Post(action);

    // ---------- Événements vers la vue ----------

    public event Action<int, int>? SelectionRequested;

    public event EventHandler<ExternalChangeEventArgs>? ConflictDetected;

    // ---------- Services exposés à la vue ----------

    public GitPanelViewModel Git { get; }

    // ---------- Dossier / documents ----------

    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private bool _hasFolder;
    [ObservableProperty] private string _statusMessage = "Prêt";
    [ObservableProperty] private string _position = "Ln 1, Col 1";
    [ObservableProperty] private string _language = "";
    [ObservableProperty] private string _encoding = "";
    [ObservableProperty] private string _fileContent = "";

    public ObservableCollection<DocumentTab> Tabs { get; } = new();
    public ObservableCollection<FileNode> Nodes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private DocumentTab? _activeTab;

    public bool HasFile => ActiveTab is not null;

    partial void OnActiveTabChanged(
        DocumentTab? oldValue,
        DocumentTab? newValue)
    {
        if (oldValue is not null)
            oldValue.IsActive = false;

        if (newValue is not null)
            newValue.IsActive = true;

        IsFindVisible &= newValue is not null;
        RefreshEditorView();
    }

    // ---------- Journal (onglet Sortie) ----------

    [ObservableProperty] private string _outputText = "";

    public void Log(string message) =>
        OutputText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";

    private void ReportError(AppException ex)
    {
        StatusMessage = ex.Message;
        Log("Erreur : " + ex.Message);
    }

    // ---------- Commandes en attente des autres modules ----------

    [RelayCommand]
    private void NotAvailable(string? info)
    {
        var parts = (info ?? "").Split('|');
        var owner = parts.Length > 1
            ? parts[0]
            : "module concerné";

        var name = parts.Length > 1
            ? parts[1]
            : info;

        StatusMessage =
            $"« {name} » sera disponible avec le module du {owner}.";

        Log(StatusMessage);
    }

    // =====================================================================
    // Espace de travail
    // =====================================================================

    public async Task OpenWorkspaceAsync(string path)
    {
        try
        {
            await _s.Workspace.OpenWorkspaceAsync(path);
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    private void ApplyWorkspace(WorkspaceInfo? info)
    {
        Nodes.Clear();

        if (info is null)
        {
            HasFolder = false;
            FolderName = "";
            RunSearch();
            return;
        }

        LoadInto(Nodes, info.RootPath, 0);

        FolderName = info.Name.ToUpperInvariant();
        HasFolder = true;
        StatusMessage = "Dossier ouvert : " + info.RootPath;

        Log(StatusMessage);
        RunSearch();

        _ = Git.RefreshCommand.ExecuteAsync(null);
    }

    private static void LoadInto(
        ObservableCollection<FileNode> target,
        string path,
        int depth)
    {
        if (depth > 5)
            return;

        try
        {
            foreach (var dir in System.IO.Directory
                         .GetDirectories(path)
                         .OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dir);

                if (Ignored.Contains(name))
                    continue;

                var node = new FileNode(name, dir, true);
                LoadInto(node.Children, dir, depth + 1);
                target.Add(node);
            }

            foreach (var file in System.IO.Directory
                         .GetFiles(path)
                         .OrderBy(Path.GetFileName))
            {
                target.Add(new FileNode(
                    Path.GetFileName(file),
                    file,
                    false));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    [RelayCommand]
    private void RefreshExplorer()
    {
        if (_s.Workspace.Current is { } ws)
            ApplyWorkspace(ws);
    }

    // =====================================================================
    // Documents et onglets (IEditorService)
    // =====================================================================

    public async Task OpenFileAsync(FileNode node)
    {
        if (node.IsDirectory)
            return;

        await OpenPathAsync(node.FullPath);
    }

    public async Task OpenPathAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 2_000_000)
            {
                StatusMessage = "Fichier trop volumineux (plus de 2 Mo)";
                Log(StatusMessage);
                return;
            }

            var info = await _s.Editor.OpenDocumentAsync(path);
            ActiveTab = AddTab(info);

            Log("Fichier ouvert : " + path);
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
        catch (IOException ex)
        {
            StatusMessage = "Lecture impossible : " + ex.Message;
            Log(StatusMessage);
        }
    }

    [RelayCommand]
    private void NewFile()
    {
        ActiveTab = AddTab(_s.Editor.CreateDocument());
        StatusMessage = "Nouveau fichier";
    }

    private DocumentTab AddTab(DocumentInfo info)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == info.Id);

        if (tab is null)
        {
            tab = new DocumentTab(info);
            Tabs.Add(tab);
        }

        return tab;
    }

    private void RemoveTab(DocumentId id)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);

        if (tab is null)
            return;

        int index = Tabs.IndexOf(tab);
        bool wasActive = ReferenceEquals(tab, ActiveTab);

        Tabs.Remove(tab);

        if (wasActive)
        {
            ActiveTab = Tabs.Count == 0
                ? null
                : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    public void CloseTab(DocumentTab tab)
    {
        _s.Editor.CloseDocument(tab.Id);
        RemoveTab(tab.Id);

        if (!HasFile)
            StatusMessage = "Prêt";
    }

    private void OnDocumentState(DocumentInfo info)
    {
        Tabs.FirstOrDefault(t => t.Id == info.Id)?.Apply(info);

        if (ActiveTab?.Id == info.Id)
        {
            Language = DisplayLanguage(info.LanguageId);

            if (!info.IsDirty)
                _ = Git.RefreshCommand.ExecuteAsync(null);
        }
    }

    private void OnDocumentChanged(DocumentId id)
    {
        if (ActiveTab?.Id != id)
            return;

        try
        {
            if (_s.Editor.GetText(id) != FileContent)
                RefreshEditorView();
        }
        catch (AppException)
        {
        }
    }

    public void RefreshEditorView()
    {
        if (ActiveTab is null)
        {
            FileContent = "";
            Language = "";
            Encoding = "";
            Position = "Ln 1, Col 1";
            return;
        }

        var id = ActiveTab.Id;

        try
        {
            FileContent = _s.Editor.GetText(id);
            Language = DisplayLanguage(
                _s.Editor.GetInfo(id).LanguageId);

            Encoding = _s.Editor.GetEncoding(id).DisplayName;

            var caret = _s.Editor.GetCaretSet(id).Primary;

            int anchor = TextPositions.ToOffset(
                FileContent,
                caret.Anchor);

            int active = TextPositions.ToOffset(
                FileContent,
                caret.Position);

            Position =
                $"Ln {caret.Position.Line + 1}, Col {caret.Position.Column + 1}";

            SelectionRequested?.Invoke(anchor, active);
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    public void UpdateCaret(string? text, int caretIndex)
    {
        text ??= "";

        var p = TextPositions.ToPosition(text, caretIndex);

        Position = $"Ln {p.Line + 1}, Col {p.Column + 1}";
    }

    // ---------- Édition (IEditorCommands) ----------

    public void SyncSelection(
        int selectionStart,
        int selectionEnd,
        int caretIndex)
    {
        if (ActiveTab is null)
            return;

        string text = FileContent;
        int active = caretIndex;

        int anchor = selectionStart == selectionEnd
            ? caretIndex
            : (caretIndex == selectionEnd
                ? selectionStart
                : selectionEnd);

        try
        {
            var caret = new Caret(
                TextPositions.ToPosition(text, active),
                TextPositions.ToPosition(text, anchor));

            _s.Editor.SetCaretSet(
                ActiveTab.Id,
                new CaretSet([caret]));
        }
        catch (ArgumentException)
        {
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    public void ExecuteEdit(
        Action<IEditorCommands, DocumentId> action)
    {
        if (ActiveTab is null)
            return;

        try
        {
            action(_s.EditorCommands, ActiveTab.Id);
            RefreshEditorView();
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
        }
    }

    public string? CopyText()
    {
        if (ActiveTab is null)
            return null;

        try
        {
            return _s.EditorCommands.Copy(ActiveTab.Id);
        }
        catch (AppException ex)
        {
            ReportError(ex);
            return null;
        }
    }

    public string? CutText()
    {
        if (ActiveTab is null)
            return null;

        try
        {
            string text = _s.EditorCommands.Cut(ActiveTab.Id);
            RefreshEditorView();
            return text;
        }
        catch (AppException ex)
        {
            ReportError(ex);
            return null;
        }
    }

    [RelayCommand]
    private void Undo() =>
        ExecuteEdit((_, id) => _s.Editor.Undo(id));

    [RelayCommand]
    private void Redo() =>
        ExecuteEdit((_, id) => _s.Editor.Redo(id));

    [RelayCommand]
    private void SelectAllText() =>
        ExecuteEdit((c, id) => c.SelectAll(id));

    public void GoToLine(int line)
    {
        if (ActiveTab is null)
            return;

        int offset = TextPositions.ToOffset(
            FileContent,
            new TextPosition(Math.Max(0, line - 1), 0));

        try
        {
            _s.Editor.SetCaretSet(
                ActiveTab.Id,
                CaretSet.Single(
                    TextPositions.ToPosition(FileContent, offset)));

            RefreshEditorView();
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    // ---------- Enregistrement ----------

    public async Task<bool> SaveActiveAsync()
    {
        if (ActiveTab is null || ActiveTab.Path is null)
            return false;

        return await SaveAsync(ActiveTab);
    }

    public async Task<bool> SaveAsync(DocumentTab tab)
    {
        try
        {
            await _s.Editor.SaveAsync(tab.Id);

            StatusMessage = "Enregistré : " + tab.Title;
            Log(StatusMessage);

            return true;
        }
        catch (AppException ex)
        {
            ReportError(ex);
            return false;
        }
    }

    public async Task<bool> SaveAsAsync(
        DocumentTab tab,
        string path)
    {
        try
        {
            await _s.Editor.SaveAsAsync(tab.Id, path);

            StatusMessage = "Enregistré sous : " + path;
            Log(StatusMessage);

            RefreshEditorView();

            return true;
        }
        catch (AppException ex)
        {
            ReportError(ex);
            return false;
        }
    }

    // ---------- Changements externes ----------

    private void OnExternalChange(ExternalChangeEventArgs e)
    {
        string name = Path.GetFileName(e.Path);

        switch (e.Kind)
        {
            case ExternalChangeKind.Reloaded:
                StatusMessage =
                    $"« {name} » rechargé (modifié sur le disque)";
                Log(StatusMessage);
                break;

            case ExternalChangeKind.Deleted:
                StatusMessage =
                    $"« {name} » a été supprimé ou renommé sur le disque";
                Log(StatusMessage);
                break;

            case ExternalChangeKind.Conflict:
                ConflictDetected?.Invoke(this, e);
                break;
        }
    }

    public async Task ResolveConflictAsync(
        DocumentId id,
        bool reloadFromDisk)
    {
        try
        {
            if (reloadFromDisk)
                await _s.Editor.ReloadFromDiskAsync(id);
            else
                await _s.Editor.KeepLocalVersionAsync(id);
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    public async Task CheckExternalChangesAsync()
    {
        foreach (var tab in Tabs
                     .Where(t => t.Path is not null)
                     .ToList())
        {
            try
            {
                await _s.Editor.CheckExternalChangeAsync(tab.Id);
            }
            catch (AppException)
            {
            }
        }
    }

    // =====================================================================
    // Rechercher / remplacer dans le document
    // =====================================================================

    [ObservableProperty] private bool _isFindVisible;
    [ObservableProperty] private string _findInfo = "";
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceText = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _useRegex;

    partial void OnFindTextChanged(string value) =>
        UpdateFindCount();

    partial void OnMatchCaseChanged(bool value) =>
        UpdateFindCount();

    partial void OnUseRegexChanged(bool value) =>
        UpdateFindCount();

    private SearchOptions Options =>
        new(MatchCase: MatchCase, UseRegex: UseRegex);

    public void UpdateFindCount()
    {
        if (ActiveTab is null || FindText.Length == 0)
        {
            FindInfo = "";
            return;
        }

        try
        {
            int count = _s.EditorCommands
                .FindAll(ActiveTab.Id, FindText, Options)
                .Count;

            FindInfo = count == 0
                ? "Aucun résultat"
                : $"{count} résultat(s)";
        }
        catch (InvalidOperationException)
        {
            FindInfo = "Expression invalide";
        }
        catch (AppException)
        {
            FindInfo = "Expression invalide";
        }
    }

    public void FindNext(bool forward)
    {
        if (ActiveTab is null || FindText.Length == 0)
        {
            FindInfo = "";
            return;
        }

        var id = ActiveTab.Id;

        try
        {
            var range = _s.Editor.GetCaretSet(id)
                .Primary.Selection.Range;

            TextRange? hit = forward
                ? _s.EditorCommands.FindNext(
                    id, FindText, range.End, Options)
                : _s.EditorCommands.FindPrevious(
                    id, FindText, range.Start, Options);

            UpdateFindCount();

            if (hit is null)
                return;

            _s.Editor.SetCaretSet(
                id,
                new CaretSet([
                    new Caret(hit.Value.End, hit.Value.Start)
                ]));

            RefreshEditorView();
        }
        catch (InvalidOperationException)
        {
            FindInfo = "Expression invalide";
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand]
    private void ReplaceNext()
    {
        if (ActiveTab is null || FindText.Length == 0)
            return;

        ExecuteEdit((c, id) =>
            c.ReplaceNext(id, FindText, ReplaceText, Options));

        UpdateFindCount();
    }

    [RelayCommand]
    private void ReplaceAll()
    {
        if (ActiveTab is null || FindText.Length == 0)
            return;

        int count = 0;

        ExecuteEdit((c, id) =>
            count = c.ReplaceAll(
                id, FindText, ReplaceText, Options));

        StatusMessage = $"{count} remplacement(s)";
        UpdateFindCount();
    }

    // =====================================================================
    // Panneau du bas
    // =====================================================================

    [ObservableProperty] private bool _isBottomVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsTab))]
    [NotifyPropertyChangedFor(nameof(IsOutputTab))]
    [NotifyPropertyChangedFor(nameof(IsTerminalTab))]
    private int _bottomTab;

    public bool IsProblemsTab => BottomTab == 0;
    public bool IsOutputTab => BottomTab == 1;
    public bool IsTerminalTab => BottomTab == 2;

    [RelayCommand]
    private void ShowBottom(string? index)
    {
        if (!int.TryParse(index, out var n))
            return;

        if (BottomTab == n && IsBottomVisible)
        {
            IsBottomVisible = false;
            return;
        }

        BottomTab = n;
        IsBottomVisible = true;

        if (n == 2)
            _ = EnsureTerminalAsync();
    }

    [RelayCommand]
    private void SetBottomTab(string? index)
    {
        if (!int.TryParse(index, out var n))
            return;

        BottomTab = n;

        if (n == 2)
            _ = EnsureTerminalAsync();
    }

    [RelayCommand]
    private void ToggleBottom()
    {
        IsBottomVisible = !IsBottomVisible;

        if (IsBottomVisible && IsTerminalTab)
            _ = EnsureTerminalAsync();
    }

    [RelayCommand]
    private void CloseBottom() =>
        IsBottomVisible = false;

    public void ShowOutput()
    {
        BottomTab = 1;
        IsBottomVisible = true;
    }

    // ---------- Problèmes ----------

    public ObservableCollection<ProblemItem> Problems { get; } = new();

    [ObservableProperty]
    private string _problemsSummary = "Aucun problème détecté.";

    private void OnDiagnostics(DiagnosticsPublishedEventArgs e)
    {
        foreach (var old in Problems
                     .Where(p => string.Equals(
                         p.Path,
                         e.Path,
                         StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            Problems.Remove(old);
        }

        string file = Path.GetFileName(e.Path);

        foreach (var d in e.Diagnostics)
        {
            string severity = d.Severity switch
            {
                DiagnosticSeverity.Error => "Erreur",
                DiagnosticSeverity.Warning => "Avertissement",
                DiagnosticSeverity.Information => "Info",
                _ => "Astuce"
            };

            Problems.Add(new ProblemItem(
                e.Path,
                file,
                d.Range.Start.Line + 1,
                d.Range.Start.Column + 1,
                severity,
                d.Message));
        }

        ProblemsSummary = Problems.Count == 0
            ? "Aucun problème détecté."
            : $"{Problems.Count} problème(s)";
    }

    public async Task OpenProblemAsync(ProblemItem problem)
    {
        await OpenPathAsync(problem.Path);
        GoToLine(problem.Line);
    }

    // =====================================================================
    // Terminal : véritable pseudo-terminal avec échange en octets
    // =====================================================================

    public ITerminalService TerminalService => _s.Terminal;

    public TerminalSessionId? ActiveTerminalSessionId =>
        _terminalSessionId;

    [ObservableProperty]
    private bool _isTerminalRunning;

    [ObservableProperty]
    private string _terminalStatus = "Aucun terminal actif.";

    /// <summary>
    /// Démarre le terminal si aucune session n'est active.
    /// </summary>
    public async Task EnsureTerminalAsync()
    {
        if (IsTerminalRunning || _terminalSessionId.HasValue)
            return;

        await StartTerminal();
    }

    [RelayCommand]
    private async Task StartTerminal()
    {
        if (IsTerminalRunning || _terminalSessionId.HasValue)
        {
            TerminalStatus = "Un terminal est déjà actif.";
            return;
        }

        try
        {
            string? workingDirectory =
                _s.Workspace.Current?.RootPath;

            var options = new TerminalStartOptions(
                WorkingDirectory: workingDirectory);

            var sessionId =
                await _s.Terminal.StartSessionAsync(options);

            _terminalSessionId = sessionId;

            OnPropertyChanged(nameof(ActiveTerminalSessionId));

            IsTerminalRunning = true;

            TerminalStatus = workingDirectory is null
                ? "Terminal actif"
                : $"Terminal actif — {workingDirectory}";

            Log("Terminal démarré");
        }
        catch (Exception ex)
        {
            _terminalSessionId = null;
            IsTerminalRunning = false;

            OnPropertyChanged(nameof(ActiveTerminalSessionId));

            TerminalStatus = "Impossible de démarrer le terminal.";

            Log("Terminal indisponible : " + ex.Message);
        }
    }

    /// <summary>
    /// Transmet directement les octets reçus du contrôle terminal au PTY.
    /// Cela permet au shell de gérer lui-même les touches, l'historique,
    /// les raccourcis clavier et les séquences de contrôle.
    /// </summary>
    public async Task SendTerminalInputBytesAsync(
        ReadOnlyMemory<byte> input)
    {
        if (_terminalSessionId is not { } sessionId ||
            !IsTerminalRunning ||
            input.IsEmpty)
        {
            return;
        }

        try
        {
            await _s.Terminal.SendInputBytesAsync(
                sessionId,
                input);
        }
        catch (AppException ex)
        {
            ReportError(ex);
        }
        catch (Exception ex)
        {
            TerminalStatus =
                "Erreur lors de l'envoi des données au terminal.";

            Log("Erreur terminal : " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopTerminal()
    {
        if (_terminalSessionId is not { } sessionId)
            return;

        _terminalSessionId = null;

        OnPropertyChanged(nameof(ActiveTerminalSessionId));

        IsTerminalRunning = false;

        try
        {
            await _s.Terminal.StopSessionAsync(sessionId);

            TerminalStatus = "Terminal arrêté.";
            Log("Terminal arrêté");
        }
        catch (Exception ex)
        {
            TerminalStatus = "Erreur lors de l'arrêt du terminal.";
            Log("Erreur terminal : " + ex.Message);
        }
    }

    // =====================================================================
    // Barre latérale
    // =====================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorerActive))]
    [NotifyPropertyChangedFor(nameof(IsSearchActive))]
    [NotifyPropertyChangedFor(nameof(IsGitActive))]
    [NotifyPropertyChangedFor(nameof(IsRunActive))]
    [NotifyPropertyChangedFor(nameof(IsExtensionsActive))]
    [NotifyPropertyChangedFor(nameof(IsPlaceholderActive))]
    [NotifyPropertyChangedFor(nameof(PanelTitle))]
    [NotifyPropertyChangedFor(nameof(PlaceholderMessage))]
    private int _activePanel;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    public bool IsExplorerActive => ActivePanel == 0;
    public bool IsSearchActive => ActivePanel == 1;
    public bool IsGitActive => ActivePanel == 2;
    public bool IsRunActive => ActivePanel == 3;
    public bool IsExtensionsActive => ActivePanel == 4;
    public bool IsPlaceholderActive => false;

    public string PanelTitle => ActivePanel switch
    {
        0 => "EXPLORATEUR",
        1 => "RECHERCHE",
        2 => "CONTRÔLE DE CODE SOURCE",
        3 => "EXÉCUTER ET DÉBOGUER",
        _ => "EXTENSIONS"
    };

    public string PlaceholderMessage => "";

    [RelayCommand]
    private void ShowPanel(string? index)
    {
        if (!int.TryParse(index, out var n))
            return;

        if (ActivePanel == n && IsSidebarVisible)
        {
            IsSidebarVisible = false;
            return;
        }

        ActivePanel = n;
        IsSidebarVisible = true;

        if (n == 2)
            _ = Git.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void ToggleSidebar() =>
        IsSidebarVisible = !IsSidebarVisible;

    // ---------- Recherche par nom de fichier ----------

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty]
    private string _searchInfo = "Ouvrez un dossier pour rechercher.";

    public ObservableCollection<FileNode> SearchResults { get; } = new();

    partial void OnSearchQueryChanged(string value) =>
        RunSearch();

    private void RunSearch()
    {
        SearchResults.Clear();

        var q = SearchQuery.Trim();

        if (!HasFolder)
        {
            SearchInfo = "Ouvrez un dossier pour rechercher.";
            return;
        }

        if (q.Length == 0)
        {
            SearchInfo = "Tapez un nom de fichier.";
            return;
        }

        Collect(Nodes, q);

        SearchInfo = SearchResults.Count == 0
            ? "Aucun fichier trouvé."
            : $"{SearchResults.Count} fichier(s) trouvé(s)";
    }

    private void Collect(
        IEnumerable<FileNode> nodes,
        string q)
    {
        foreach (var n in nodes)
        {
            if (SearchResults.Count >= 200)
                return;

            if (!n.IsDirectory &&
                n.Name.Contains(
                    q,
                    StringComparison.OrdinalIgnoreCase))
            {
                SearchResults.Add(n);
            }

            Collect(n.Children, q);
        }
    }

    private static string DisplayLanguage(string languageId) =>
        languageId switch
        {
            "csharp" => "C#",
            "python" => "Python",
            "javascript" => "JavaScript",
            "javascriptreact" => "JavaScript React",
            "typescript" => "TypeScript",
            "typescriptreact" => "TypeScript React",
            "json" or "jsonc" => "JSON",
            "markdown" => "Markdown",
            "html" => "HTML",
            "css" or "scss" => "CSS",
            "xml" => "XML",
            "yaml" => "YAML",
            "shellscript" => "Shell",
            "powershell" => "PowerShell",
            "plaintext" or "" => "Texte brut",
            _ => char.ToUpperInvariant(languageId[0]) + languageId[1..]
        };
}

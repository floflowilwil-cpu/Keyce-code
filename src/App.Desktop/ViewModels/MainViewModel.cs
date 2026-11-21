using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly HashSet<string> Ignored =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };

    // ---------- Dossier / fichier ----------
    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private bool _hasFolder;
    [ObservableProperty] private string _statusMessage = "Prêt";
    [ObservableProperty] private string _position = "Ln 1, Col 1";
    [ObservableProperty] private string _language = "";
    [ObservableProperty] private string _lineEnding = "";
    [ObservableProperty] private string _fileContent = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string? _openFileName;

    public bool HasFile => OpenFileName is not null;
    public ObservableCollection<FileNode> Nodes { get; } = new();

    // ---------- Journal (onglet Sortie) ----------
    [ObservableProperty] private string _outputText = "";

    public void Log(string message) =>
        OutputText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";

    // ---------- Commandes en attente des autres modules ----------
    [RelayCommand]
    private void NotAvailable(string? info)
    {
        var parts = (info ?? "").Split('|');
        var owner = parts.Length > 1 ? parts[0] : "module concerné";
        var name = parts.Length > 1 ? parts[1] : info;
        StatusMessage = $"« {name} » sera disponible avec le module du {owner}.";
        Log(StatusMessage);
    }

    // ---------- Barre de recherche dans le fichier ----------
    [ObservableProperty] private bool _isFindVisible;
    [ObservableProperty] private string _findInfo = "";

    // ---------- Panneau du bas ----------
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
        if (!int.TryParse(index, out var n)) return;
        if (BottomTab == n && IsBottomVisible) { IsBottomVisible = false; return; }
        BottomTab = n;
        IsBottomVisible = true;
    }

    [RelayCommand]
    private void SetBottomTab(string? index)
    {
        if (int.TryParse(index, out var n)) BottomTab = n;
    }

    [RelayCommand] private void ToggleBottom() => IsBottomVisible = !IsBottomVisible;
    [RelayCommand] private void CloseBottom() => IsBottomVisible = false;

    // ---------- Panneaux de la barre latérale ----------
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

    [ObservableProperty] private bool _isSidebarVisible = true;

    public bool IsExplorerActive => ActivePanel == 0;
    public bool IsSearchActive => ActivePanel == 1;
    public bool IsGitActive => ActivePanel == 2;
    public bool IsRunActive => ActivePanel == 3;
    public bool IsExtensionsActive => ActivePanel == 4;
    public bool IsPlaceholderActive => ActivePanel >= 2;

    public string PanelTitle => ActivePanel switch
    {
        0 => "EXPLORATEUR",
        1 => "RECHERCHE",
        2 => "CONTRÔLE DE CODE SOURCE",
        3 => "EXÉCUTER ET DÉBOGUER",
        _ => "EXTENSIONS"
    };

    public string PlaceholderMessage => ActivePanel switch
    {
        2 => "Ce panneau sera connecté au module Git (développeur 4).",
        3 => "Ce panneau sera connecté au module d'exécution et de débogage (développeur 5).",
        4 => "Ce panneau sera connecté au gestionnaire d'extensions (développeur 4).",
        _ => ""
    };

    [RelayCommand]
    private void ShowPanel(string? index)
    {
        if (!int.TryParse(index, out var n)) return;
        if (ActivePanel == n && IsSidebarVisible)
        {
            IsSidebarVisible = false;
            return;
        }
        ActivePanel = n;
        IsSidebarVisible = true;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    // ---------- Recherche par nom de fichier ----------
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchInfo = "Ouvrez un dossier pour rechercher.";
    public ObservableCollection<FileNode> SearchResults { get; } = new();

    partial void OnSearchQueryChanged(string value) => RunSearch();

    private void RunSearch()
    {
        SearchResults.Clear();
        var q = SearchQuery.Trim();
        if (!HasFolder) { SearchInfo = "Ouvrez un dossier pour rechercher."; return; }
        if (q.Length == 0) { SearchInfo = "Tapez un nom de fichier."; return; }
        Collect(Nodes, q);
        SearchInfo = SearchResults.Count == 0
            ? "Aucun fichier trouvé."
            : $"{SearchResults.Count} fichier(s) trouvé(s)";
    }

    private void Collect(IEnumerable<FileNode> nodes, string q)
    {
        foreach (var n in nodes)
        {
            if (SearchResults.Count >= 200) return;
            if (!n.IsDirectory && n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                SearchResults.Add(n);
            Collect(n.Children, q);
        }
    }

    // ---------- Dossier ----------
    public void OpenFolder(string path)
    {
        Nodes.Clear();
        LoadInto(Nodes, path, 0);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        FolderName = (string.IsNullOrEmpty(name) ? path : name).ToUpperInvariant();
        HasFolder = true;
        StatusMessage = "Dossier ouvert : " + path;
        Log(StatusMessage);
        RunSearch();
    }

    private static void LoadInto(ObservableCollection<FileNode> target, string path, int depth)
    {
        if (depth > 5) return;
        try
        {
            foreach (var dir in System.IO.Directory.GetDirectories(path).OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dir);
                if (Ignored.Contains(name)) continue;
                var node = new FileNode(name, dir, true);
                LoadInto(node.Children, dir, depth + 1);
                target.Add(node);
            }
            foreach (var file in System.IO.Directory.GetFiles(path).OrderBy(Path.GetFileName))
                target.Add(new FileNode(Path.GetFileName(file), file, false));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    // ---------- Fichier ----------
    public void OpenFile(FileNode node)
    {
        if (node.IsDirectory) return;
        try
        {
            if (new FileInfo(node.FullPath).Length > 2_000_000)
            {
                StatusMessage = "Fichier trop volumineux (plus de 2 Mo)";
                Log(StatusMessage);
                return;
            }
            var text = File.ReadAllText(node.FullPath);
            if (text.Contains('\0'))
            {
                StatusMessage = "Fichier binaire : affichage impossible";
                Log(StatusMessage);
                return;
            }
            FileContent = text;
            OpenFileName = node.Name;
            LineEnding = text.Contains("\r\n") ? "CRLF" : "LF";
            Language = DetectLanguage(node.Name);
            Position = "Ln 1, Col 1";
            StatusMessage = node.FullPath;
            Log("Fichier ouvert : " + node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Lecture impossible : " + ex.Message;
            Log(StatusMessage);
        }
    }

    [RelayCommand]
    private void CloseFile()
    {
        OpenFileName = null;
        FileContent = "";
        Language = "";
        LineEnding = "";
        Position = "Ln 1, Col 1";
        IsFindVisible = false;
        StatusMessage = "Prêt";
    }

    public void UpdateCaret(string? text, int caretIndex)
    {
        text ??= "";
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);
        int line = 1, lastNewLine = -1;
        for (int i = 0; i < caretIndex; i++)
        {
            if (text[i] == '\n') { line++; lastNewLine = i; }
        }
        Position = $"Ln {line}, Col {caretIndex - lastNewLine}";
    }

    private static string DetectLanguage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".axaml" or ".xaml" => "XAML",
            ".csproj" or ".xml" => "XML",
            ".json" => "JSON",
            ".md" => "Markdown",
            ".py" => "Python",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            ".kt" or ".kts" => "Kotlin",
            ".html" => "HTML",
            ".css" => "CSS",
            _ => "Texte brut"
        };
}
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
    public bool IsDirectory { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    public FileNode(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }
}

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly HashSet<string> Ignored =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };

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

    public void OpenFolder(string path)
    {
        Nodes.Clear();
        LoadInto(Nodes, path, 0);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        FolderName = (string.IsNullOrEmpty(name) ? path : name).ToUpperInvariant();
        HasFolder = true;
        StatusMessage = "Dossier ouvert : " + path;
    }

    private static void LoadInto(ObservableCollection<FileNode> target, string path, int depth)
    {
        if (depth > 5) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(path).OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dir);
                if (Ignored.Contains(name)) continue;
                var node = new FileNode(name, dir, true);
                LoadInto(node.Children, dir, depth + 1);
                target.Add(node);
            }
            foreach (var file in Directory.GetFiles(path).OrderBy(Path.GetFileName))
                target.Add(new FileNode(Path.GetFileName(file), file, false));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    public void OpenFile(FileNode node)
    {
        if (node.IsDirectory) return;
        try
        {
            if (new FileInfo(node.FullPath).Length > 2_000_000)
            {
                StatusMessage = "Fichier trop volumineux (plus de 2 Mo)";
                return;
            }
            var text = File.ReadAllText(node.FullPath);
            if (text.Contains('\0'))
            {
                StatusMessage = "Fichier binaire : affichage impossible";
                return;
            }
            FileContent = text;
            OpenFileName = node.Name;
            LineEnding = text.Contains("\r\n") ? "CRLF" : "LF";
            Language = DetectLanguage(node.Name);
            Position = "Ln 1, Col 1";
            StatusMessage = node.FullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Lecture impossible : " + ex.Message;
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
            ".html" => "HTML",
            ".css" => "CSS",
            _ => "Texte brut"
        };
}
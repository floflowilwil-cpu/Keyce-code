using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace App.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, string> filePaths = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenFolder_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;

        if (storage == null)
            return;

        var folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choisir un dossier",
                AllowMultiple = false
            });

        if (folders.Count == 0)
            return;

        string folderPath = folders[0].Path.LocalPath;

        if (!Directory.Exists(folderPath))
            return;

        FolderName.Text = Path.GetFileName(folderPath);

        filePaths.Clear();

        var entries = Directory.GetDirectories(folderPath)
            .Select(path => AddEntry(path, true))
            .Concat(
                Directory.GetFiles(folderPath)
                    .Select(path => AddEntry(path, false))
            )
            .ToList();

        ExplorerList.ItemsSource = entries;
        EditorTextBox.Text = "";
        EditorTitle.Text = FolderName.Text;
    }

    private string AddEntry(string path, bool isFolder)
    {
        string name = Path.GetFileName(path);
        string display = (isFolder ? "📁 " : "📄 ") + name;

        filePaths[display] = path;

        return display;
    }

    private void ExplorerList_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (ExplorerList.SelectedItem is not string selected)
            return;

        if (!filePaths.TryGetValue(selected, out string? path))
            return;

        if (!File.Exists(path))
            return;

        try
        {
            EditorTextBox.Text = File.ReadAllText(path);
            EditorTitle.Text = Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            EditorTextBox.Text = "Erreur : " + ex.Message;
        }
    }
}
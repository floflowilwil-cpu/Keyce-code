using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using App.Desktop.ViewModels;

namespace App.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        EditorBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty && DataContext is MainWindowViewModel vm)
                vm.UpdateCaret(EditorBox.Text, EditorBox.CaretIndex);
        };
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Ouvrir un dossier",
            AllowMultiple = false
        });
        if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
            vm.OpenFolder(folders[0].Path.LocalPath);
    }

    private void OnNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is FileNode node
            && DataContext is MainWindowViewModel vm)
            vm.OpenFile(node);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
using App.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.Desktop.ViewModels;

/// <summary>Un onglet = un document ouvert dans IEditorService (l'état vient du service, pas de l'interface).</summary>
public sealed partial class DocumentTab : ObservableObject
{
    public DocumentTab(DocumentInfo info)
    {
        Id = info.Id;
        Apply(info);
    }

    public DocumentId Id { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isActive;

    public void Apply(DocumentInfo info)
    {
        Path = info.Path;
        IsDirty = info.IsDirty;
        Title = info.Path is null ? "Sans titre" : System.IO.Path.GetFileName(info.Path);
    }
}

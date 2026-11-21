using System.Linq;
using App.Core.Models;

namespace App.Desktop.ViewModels;

/// <summary>Une ligne de la liste des extensions (lecture seule, reconstruite à chaque changement).</summary>
public sealed class ExtensionItem(ExtensionInfo info)
{
    public string Id => info.Manifest.Id;
    public string Title => $"{info.Manifest.Name}  v{info.Manifest.Version}";
    public string Description => info.Manifest.Description;
    public string Permissions => info.Manifest.Permissions.Count == 0
        ? "Aucune permission"
        : "Permissions : " + string.Join(", ", info.Manifest.Permissions);
    public bool IsActive => info.State == ExtensionState.Active;
    public string StateText => IsActive ? "Active" : "Installée";
    public string ToggleLabel => IsActive ? "Désactiver" : "Activer";
}

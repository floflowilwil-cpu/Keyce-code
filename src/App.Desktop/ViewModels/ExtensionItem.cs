using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using App.Core.Models;

namespace App.Desktop.ViewModels;

/// <summary>Une ligne de la liste des extensions installées.</summary>
public sealed class ExtensionItem(
    ExtensionInfo info,
    string? availableUpdateVersion = null,
    bool showInstallProof = false)
{
    public string Id => info.Manifest.Id;
    public string Title => $"{info.Manifest.Name}  v{info.Manifest.Version}";
    public string Description => info.Manifest.Description;
    public string Permissions => info.Manifest.Permissions.Count == 0
        ? "Aucune permission"
        : "Permissions : " + string.Join(", ", info.Manifest.Permissions);
    public string InstallPath => info.InstallPath;
    public bool HasReadme =>
        File.Exists(Path.Combine(info.InstallPath, "README.md"))
        || File.Exists(Path.Combine(info.InstallPath, "package.json"));
    public bool ShowInstallProof => showInstallProof;
    public bool ShowReadmeButton => showInstallProof && HasReadme;
    public string PackageDetails { get; } = showInstallProof
        ? BuildPackageDetails(info.InstallPath)
        : string.Empty;
    public bool IsActive => info.State == ExtensionState.Active;
    public string StateText => IsActive ? "Active" : "Installée";
    public string ToggleLabel => IsActive ? "Désactiver" : "Activer";
    public bool HasUpdate => !string.IsNullOrWhiteSpace(availableUpdateVersion);
    public string UpdateLabel => HasUpdate ? $"Mettre à jour (v{availableUpdateVersion})" : "À jour";

    private static string BuildPackageDetails(string installPath)
    {
        try
        {
            if (!Directory.Exists(installPath))
                return installPath;

            int fileCount = Directory.GetFiles(installPath, "*", SearchOption.AllDirectories)
                .Count(f => !Path.GetFileName(f).Equals("last-output.txt", StringComparison.OrdinalIgnoreCase));

            var markers = new List<string>();
            if (File.Exists(Path.Combine(installPath, "package.json")))
                markers.Add("package.json");
            if (File.Exists(Path.Combine(installPath, "README.md")))
                markers.Add("README.md");

            string extra = markers.Count > 0 ? $" • {string.Join(", ", markers)}" : string.Empty;
            return $"✓ Paquet installé sur disque ({fileCount} fichier(s){extra})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return installPath;
        }
    }
}

/// <summary>Une entrée du catalogue d'extensions disponible en 1 clic.</summary>
public sealed class CatalogExtensionItem(
    ExtensionCatalogEntry entry,
    bool isInstalled,
    bool hasUpdate)
{
    public string Id => entry.Manifest.Id;
    public string Title => $"{entry.Manifest.Name}  v{entry.Manifest.Version}";
    public string Description => entry.Manifest.Description;
    public bool CanInstall => !isInstalled;
    public bool CanUpdate => isInstalled && hasUpdate;
    public string StatusText => !isInstalled
        ? "Disponible dans le catalogue"
        : (hasUpdate ? "Nouvelle version disponible" : "Déjà installée (à jour)");
}

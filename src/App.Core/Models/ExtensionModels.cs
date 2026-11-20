namespace App.Core.Models;

public enum ExtensionState { Installed, Active }

public enum ExtensionChangeKind { Installed, Uninstalled, Activated, Deactivated, Updated }

/// <summary>Contenu du fichier extension.json d'une extension.</summary>
/// <param name="Main">Fichier .dll à charger (relatif au dossier de l'extension). Null = extension sans code.</param>
/// <param name="EntryType">Nom complet de la classe IExtension, si la .dll en contient plusieurs.</param>
/// <param name="MinAppVersion">Version minimale de l'application requise (ex: "1.0.0").</param>
/// <param name="Dependencies">Identifiants des extensions requises par cette extension.</param>
public sealed record ExtensionManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Permissions,
    string? Main = null,
    string? EntryType = null,
    string? MinAppVersion = null,
    IReadOnlyList<string>? Dependencies = null);

public sealed record ExtensionInfo(
    ExtensionManifest Manifest,
    ExtensionState State,
    string InstallPath);

public sealed record ExtensionCatalogEntry(
    ExtensionManifest Manifest,
    string PackagePath);

public sealed record ExtensionUpdateInfo(
    string ExtensionId,
    string InstalledVersion,
    string AvailableVersion,
    string PackagePath);

public sealed class ExtensionChangedEventArgs(ExtensionChangeKind kind, ExtensionInfo extension) : EventArgs
{
    public ExtensionChangeKind Kind { get; } = kind;
    public ExtensionInfo Extension { get; } = extension;
}

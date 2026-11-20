namespace App.Core.Models;

/// <summary>Permissions qu'une extension peut demander dans "permissions" de extension.json.</summary>
public static class ExtensionPermissions
{
    /// <summary>Ajouter des commandes (palette, menus).</summary>
    public const string Commands = "commands";

    /// <summary>Utiliser Git (statut, commit, push...).</summary>
    public const string Git = "git";

    /// <summary>Lire l'espace de travail ouvert.</summary>
    public const string Workspace = "workspace";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>([Commands, Git, Workspace], StringComparer.OrdinalIgnoreCase);
}

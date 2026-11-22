using System.Collections.Generic;

namespace App.Core.Models;

/// <summary>
/// Une tâche exécutable depuis « Terminal &gt; Exécuter une tâche... ».
/// <paramref name="CommandLine"/> est une ligne de commande complète, interprétée par le shell du système.
/// </summary>
/// <param name="Label">Nom affiché dans la liste.</param>
/// <param name="CommandLine">Commande complète (par exemple « dotnet build »).</param>
/// <param name="WorkingDirectory">Dossier d'exécution (déjà résolu). Null = racine de l'espace de travail.</param>
/// <param name="Environment">Variables d'environnement supplémentaires.</param>
/// <param name="Source">Origine : « .keyce/tasks.json », « .vscode/tasks.json » ou « détectée ».</param>
public sealed record TaskDefinition(
    string Label,
    string CommandLine,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string Source = "");

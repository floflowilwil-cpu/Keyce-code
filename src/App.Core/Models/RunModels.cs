using System.Collections.Generic;

namespace App.Core.Models;

/// <summary>Une étape d'un plan d'exécution (par exemple « compiler », puis « lancer »).</summary>
public sealed record RunStep(
    string Description,
    ExecutionStartOptions Options);

/// <summary>
/// Plan complet pour exécuter un fichier : langage détecté et étapes à enchaîner.
/// Les étapes sont exécutées dans l'ordre ; on s'arrête à la première qui échoue.
/// </summary>
public sealed record RunPlan(
    string LanguageId,
    string DisplayName,
    IReadOnlyList<RunStep> Steps);

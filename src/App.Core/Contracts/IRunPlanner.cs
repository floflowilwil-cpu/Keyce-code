using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Détecte le langage d'un fichier et prépare les commandes pour l'exécuter ou le déboguer.
/// Utilisé par le bouton « Exécuter » de l'interface ; le lancement réel passe par
/// <see cref="IExecutionService"/> et <see cref="IDebugService"/>.
/// </summary>
public interface IRunPlanner
{
    /// <summary>
    /// Plan d'exécution du fichier (langage détecté d'après l'extension).
    /// Lève <see cref="NotSupportedException"/> si le type de fichier n'est pas exécutable
    /// et <see cref="InvalidOperationException"/> si l'interpréteur ou le compilateur est introuvable.
    /// </summary>
    RunPlan PlanRun(string filePath);

    /// <summary>Plan de compilation du projet .NET qui contient le fichier, préalable au débogage.</summary>
    RunPlan PlanDebugBuild(string filePath);

    /// <summary>Retourne le .dll compilé (Debug) du projet qui contient le fichier.</summary>
    string FindDebugAssembly(string filePath);
}

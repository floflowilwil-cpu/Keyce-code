using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>
/// Découverte et préparation des tâches d'un espace de travail (build, tests, scripts...).
/// Le lancement réel passe par <see cref="IExecutionService"/>.
/// </summary>
public interface ITaskService
{
    /// <summary>
    /// Liste les tâches : <c>.keyce/tasks.json</c>, <c>.vscode/tasks.json</c>, puis les tâches détectées
    /// (.NET, npm, cargo, go). Lève <see cref="App.Core.Errors.AppException"/> si un fichier de configuration est invalide.
    /// </summary>
    IReadOnlyList<TaskDefinition> GetTasks(string workspaceRoot);

    /// <summary>Prépare le plan d'exécution d'une tâche.</summary>
    RunPlan CreatePlan(TaskDefinition task, string workspaceRoot);

    /// <summary>Chemin du fichier <c>.keyce/tasks.json</c> de l'espace de travail (il peut ne pas exister).</summary>
    string GetConfigFilePath(string workspaceRoot);

    /// <summary>Crée <c>.keyce/tasks.json</c> avec un exemple s'il n'existe pas, puis retourne son chemin.</summary>
    string EnsureConfigFile(string workspaceRoot);
}


namespace App.Core.Models;

public readonly record struct DebugSessionId(Guid Value)
{
    public static DebugSessionId New() => new(Guid.NewGuid());
}

/// <param name="Program">
/// Programme à déboguer : assembly .NET (.dll), script Python (.py), fichier Go (.go)
/// ou exécutable natif compilé avec les symboles de débogage (C, C++, Rust).
/// </param>
/// <param name="LanguageId">
/// Identifiant du langage (« csharp », « python », « go », « c », « cpp », « rust »).
/// Si absent, il est déduit de l'extension du programme.
/// </param>
public sealed record DebugStartOptions(
    string Program,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? LanguageId = null);

/// <summary>
/// Préparation d'une session de débogage pour un fichier source : étapes de compilation
/// éventuelles et programme à lancer dans le débogueur.
/// </summary>
/// <param name="Build">Compilation préalable, ou null si le langage n'en a pas besoin.</param>
/// <param name="Program">
/// Programme à déboguer. Null pour C# : l'assembly n'existe qu'après la compilation
/// (voir <c>IRunPlanner.FindDebugAssembly</c>).
/// </param>
public sealed record DebugPlan(
    string LanguageId,
    string DisplayName,
    RunPlan? Build,
    string? Program);

public sealed record Breakpoint(string Path, int Line);

public enum StepKind { Over, Into, Out, Continue }

public sealed record DebugStopInfo(
    string Reason,
    int ThreadId,
    int? Line,
    string? SourcePath);

/// <summary>Texte produit par le programme débogué (sortie standard / erreur) et transmis par l'adaptateur.</summary>
public sealed class DebugOutputEventArgs(
    DebugSessionId sessionId,
    string category,
    string text) : EventArgs
{
    public DebugSessionId SessionId { get; } = sessionId;

    /// <summary>« stdout », « stderr » ou « console ».</summary>
    public string Category { get; } = category;

    public string Text { get; } = text;
}

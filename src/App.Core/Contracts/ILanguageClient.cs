using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Client LSP. Utilisé par UI et éditeur.</summary>
public interface ILanguageClient
{
    Task StartServerAsync(string languageId, string workspaceRoot, CancellationToken ct = default);

    Task<IReadOnlyList<CompletionItem>> CompletionAsync(string path, TextPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<SourceLocation>> DefinitionAsync(string path, TextPosition position, CancellationToken ct = default);

    event EventHandler<DiagnosticsPublishedEventArgs> DiagnosticsPublished;
}

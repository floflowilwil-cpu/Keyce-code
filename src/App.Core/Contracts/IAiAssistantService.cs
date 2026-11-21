using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Assistant IA pour le chat de code, l'explication et la correction d'erreurs.</summary>
public interface IAiAssistantService
{
    Task<AiChatResponse> ChatAsync(
        AiChatRequest request,
        CancellationToken ct = default);

    Task<string> ExplainCodeAsync(
        string code,
        string? language = null,
        CancellationToken ct = default);

    Task<string> SuggestFixAsync(
        string code,
        string diagnosticMessage,
        string? language = null,
        CancellationToken ct = default);

    Task<string> GenerateOrModifyCodeAsync(
        string instruction,
        string? currentCode = null,
        string? language = null,
        CancellationToken ct = default);
}

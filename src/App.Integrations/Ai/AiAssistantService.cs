using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Ai;

public sealed record AiAssistantOptions(
    Uri Endpoint,
    string Model,
    string? ApiKey = null,
    bool AllowOfflineFallback = false,
    IReadOnlyList<(Uri Endpoint, string Model)>? FallbackProviders = null);

public sealed partial class AiAssistantService : IAiAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly AiAssistantOptions _options;
    private int _providerCursor = -1;

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclRegex();

    [GeneratedRegex(@"\b(?:def|function)\s+([A-Za-z_][A-Za-z0-9_]*)|\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex MemberDeclRegex();

    [GeneratedRegex(@"```[^\r\n]*\r?\n([\s\S]*?)```")]
    private static partial Regex FencedCodeBlockRegex();

    [GeneratedRegex(@"(?:Retry after|wait)\s+(\d+)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterRegex();

    public AiAssistantService(HttpClient httpClient, AiAssistantOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.Endpoint is null)
            throw new ArgumentException("L'URL du service IA est obligatoire.", nameof(options));

        if (string.IsNullOrWhiteSpace(_options.Model))
            throw new ArgumentException("Le nom du modèle IA est obligatoire.", nameof(options));
    }

    public async Task<AiChatResponse> ChatAsync(
        AiChatRequest request,
        CancellationToken ct = default)
    {
        if (request is null
            || request.Messages is null
            || request.Messages.Count == 0
            || request.Messages.Any(m => m is null || string.IsNullOrWhiteSpace(m.Content)))
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Au moins un message non vide est obligatoire.");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            return await SendRemoteAsync(request, ct);
        }
        catch (AppException ex) when (_options.AllowOfflineFallback
            && ex.Code is ErrorCode.Unavailable or ErrorCode.Timeout)
        {
            return new AiChatResponse(
                BuildOfflineAnalysis(request),
                $"{_options.Model} (analyse locale)");
        }
    }

    public async Task<string> ExplainCodeAsync(
        string code,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le code à expliquer ne peut pas être vide.");

        var request = new AiChatRequest(
            [new AiChatMessage(AiChatRole.User, "Explique de manière concise ce code.")],
            new AiCodeContext(Language: language, SelectedCode: code));

        AiChatResponse response = await ChatAsync(request, ct);
        return response.Content;
    }

    public async Task<string> SuggestFixAsync(
        string code,
        string diagnosticMessage,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le code à corriger ne peut pas être vide.");

        if (string.IsNullOrWhiteSpace(diagnosticMessage))
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le message d'erreur ne peut pas être vide.");

        var request = new AiChatRequest(
            [new AiChatMessage(
                AiChatRole.User,
                $"Propose un correctif pour cette erreur : {diagnosticMessage}")],
            new AiCodeContext(Language: language, SelectedCode: code));

        AiChatResponse response = await ChatAsync(request, ct);
        return response.Content;
    }

    public async Task<string> GenerateOrModifyCodeAsync(
        string instruction,
        string? currentCode = null,
        string? language = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            throw new AppException(
                ErrorCode.InvalidArgument,
                "L'instruction de génération ou modification de code est obligatoire.");

        ct.ThrowIfCancellationRequested();

        bool hasCode = !string.IsNullOrWhiteSpace(currentCode);
        string langHint = string.IsNullOrWhiteSpace(language) ? string.Empty : $" en {language}";
        string systemInstruction = hasCode
            ? $"Tu es un développeur expert. Modifie ou complète le code fourni{langHint} selon la demande de l'utilisateur. Retourne UNIQUEMENT le code source complet final, prêt à remplacer le fichier entier, sans explications ni texte autour."
            : $"Tu es un développeur expert. Écris le code complet et fonctionnel demandé{langHint}. Retourne UNIQUEMENT le code source complet, sans explications ni texte autour.";

        AiCodeContext? context = hasCode
            ? new AiCodeContext(Language: language, SelectedCode: currentCode)
            : (string.IsNullOrWhiteSpace(language) ? null : new AiCodeContext(Language: language));

        var request = new AiChatRequest(
            [
                new AiChatMessage(AiChatRole.System, systemInstruction),
                new AiChatMessage(AiChatRole.User, instruction)
            ],
            context);

        AiChatResponse response = await SendRemoteAsync(request, ct);
        return ExtractCodeBlock(response.Content);
    }

    public static string ExtractCodeBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        MatchCollection matches = FencedCodeBlockRegex().Matches(text);
        if (matches.Count > 0)
        {
            string? best = matches
                .Select(m => m.Groups[1].Value.Trim('\r', '\n'))
                .OrderByDescending(code => code.Length)
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));

            if (best is not null)
                return best;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = trimmed.IndexOf('\n');
            if (nl >= 0)
            {
                string body = trimmed[(nl + 1)..];
                if (body.EndsWith("```", StringComparison.Ordinal))
                    body = body[..^3];
                return body.Trim('\r', '\n');
            }
        }

        return trimmed;
    }

    private async Task<AiChatResponse> SendRemoteAsync(
        AiChatRequest request,
        CancellationToken ct)
    {
        List<object> messages = BuildPayloadMessages(request);
        List<(Uri Endpoint, string Model)> candidates = BuildCandidateProviders();

        int startIndex = candidates.Count == 1
            ? 0
            : (int)((uint)Interlocked.Increment(ref _providerCursor) % (uint)candidates.Count);

        for (int offset = 0; offset < candidates.Count; offset++)
        {
            ct.ThrowIfCancellationRequested();
            (Uri endpoint, string model) = candidates[(startIndex + offset) % candidates.Count];

            try
            {
                return await SendProviderRequestAsync(endpoint, model, messages, ct);
            }
            catch (AppException) when (offset + 1 < candidates.Count)
            {
                // Bascule automatiquement sur le fournisseur/modèle suivant (ex: limite 429/402).
            }
        }

        throw new AppException(
            ErrorCode.Unavailable,
            "Aucun fournisseur IA distant n'a pu répondre.");
    }

    private List<(Uri Endpoint, string Model)> BuildCandidateProviders()
    {
        var candidates = new List<(Uri Endpoint, string Model)> { (_options.Endpoint, _options.Model) };
        if (_options.FallbackProviders is { Count: > 0 })
        {
            foreach ((Uri Endpoint, string Model) provider in _options.FallbackProviders)
            {
                if (provider.Endpoint is not null
                    && !string.IsNullOrWhiteSpace(provider.Model)
                    && !candidates.Contains(provider))
                {
                    candidates.Add(provider);
                }
            }
        }

        return candidates;
    }

    private static List<object> BuildPayloadMessages(AiChatRequest request)
    {
        var systemParts = new List<string>();
        foreach (AiChatMessage message in request.Messages.Where(m => m.Role == AiChatRole.System))
            systemParts.Add(message.Content);

        string? contextText = BuildSystemContext(request.Context);
        if (contextText is not null)
            systemParts.Add(contextText);

        var messages = new List<object>();
        if (systemParts.Count > 0)
            messages.Add(new { role = "system", content = string.Join("\n\n", systemParts) });

        foreach (AiChatMessage message in request.Messages.Where(m => m.Role != AiChatRole.System))
        {
            messages.Add(new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = message.Content
            });
        }

        return messages;
    }

    private async Task<AiChatResponse> SendProviderRequestAsync(
        Uri endpoint,
        string requestedModel,
        List<object> messages,
        CancellationToken ct)
    {
        string payloadJson = JsonSerializer.Serialize(new
        {
            model = requestedModel,
            messages,
            stream = false
        });

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey)
                && Uri.Compare(endpoint, _options.Endpoint, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                httpRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AppException(
                    ErrorCode.Unavailable,
                    $"Impossible de joindre le service IA : {ex.Message}",
                    ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AppException(
                    ErrorCode.Timeout,
                    "Le service IA a mis trop de temps à répondre.",
                    ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode is 402 or 429 && attempt < 2)
                    {
                        int? delaySeconds = await TryGetRetryDelaySecondsAsync(response, ct);
                        if (delaySeconds is >= 1 and <= 60)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds.Value + 1), ct);
                            continue;
                        }
                    }

                    throw new AppException(
                        ErrorCode.Unavailable,
                        $"Le service IA a retourné une erreur ({statusCode}).");
                }

                try
                {
                    await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                    using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    JsonElement root = doc.RootElement;

                    string? content = ExtractContent(root);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        throw new AppException(
                            ErrorCode.Unavailable,
                            "La réponse du service IA est vide.");
                    }

                    string model = root.TryGetProperty("model", out JsonElement modelProp)
                        && modelProp.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(modelProp.GetString())
                            ? modelProp.GetString()!
                            : requestedModel;

                    return new AiChatResponse(content, model);
                }
                catch (JsonException ex)
                {
                    throw new AppException(
                        ErrorCode.Unavailable,
                        "La réponse JSON du service IA est invalide.",
                        ex);
                }
            }
        }

        throw new AppException(
            ErrorCode.Unavailable,
            "Le service IA n'a pas répondu après plusieurs tentatives.");
    }

    private static async Task<int?> TryGetRetryDelaySecondsAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            int headerSeconds = (int)Math.Ceiling(delta.TotalSeconds);
            if (headerSeconds is >= 1 and <= 60)
                return headerSeconds;
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        Match match = RetryAfterRegex().Match(body);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out int parsed)
            && parsed is >= 1 and <= 60)
        {
            return parsed;
        }

        return null;
    }

    private static string BuildOfflineAnalysis(AiChatRequest request)
    {
        string code = request.Context?.SelectedCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            string prompt = request.Messages.LastOrDefault()?.Content ?? string.Empty;
            return $"Mode hors-ligne (aucun serveur Ollama détecté sur {_LocalHint}).\n"
                 + $"Question reçue : {prompt}\n"
                 + "Ouvrez un fichier de code dans l'éditeur puis relancez l'analyse pour obtenir un diagnostic détaillé.";
        }

        string[] lines = code.Replace("\r\n", "\n").Split('\n');
        int nonEmpty = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        int comments = lines.Count(l =>
        {
            string trimmed = l.TrimStart();
            return trimmed.StartsWith("//") || trimmed.StartsWith('#') || trimmed.StartsWith("/*");
        });
        int imports = lines.Count(l =>
        {
            string trimmed = l.TrimStart();
            return trimmed.StartsWith("using ") || trimmed.StartsWith("import ") || trimmed.StartsWith("from ");
        });

        List<string> types = TypeDeclRegex()
            .Matches(code)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        List<string> members = MemberDeclRegex()
            .Matches(code)
            .Select(m => !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

        bool hasAsync = code.Contains("async ", StringComparison.Ordinal)
            || code.Contains("await ", StringComparison.Ordinal);
        bool hasTryCatch = code.Contains("try", StringComparison.Ordinal)
            && (code.Contains("catch", StringComparison.Ordinal) || code.Contains("except", StringComparison.Ordinal));
        int longLines = lines.Count(l => l.Length > 120);
        bool hasTodos = code.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || code.Contains("FIXME", StringComparison.OrdinalIgnoreCase);

        string lang = string.IsNullOrWhiteSpace(request.Context?.Language)
            ? "détection auto"
            : request.Context.Language;

        var sb = new StringBuilder();
        sb.AppendLine($"[Mode analyse locale — serveur IA distant inactif]");
        sb.AppendLine($"• Résumé du code ({lang}) : {lines.Length} ligne(s) dont {nonEmpty} active(s), {imports} import(s), {comments} commentaire(s).");

        if (types.Count > 0)
            sb.AppendLine($"• Types détectés : {string.Join(", ", types)}.");

        if (members.Count > 0)
            sb.AppendLine($"• Fonctions / méthodes principales : {string.Join(", ", members)}.");

        sb.AppendLine(hasAsync
            ? "• Fonctionnement : le code utilise des appels asynchrones (async/await) pour ne pas bloquer l'exécution."
            : "• Fonctionnement : exécution synchrone séquentielle des instructions.");

        sb.AppendLine(hasTryCatch
            ? "• Gestion d'erreurs : des blocs try/catch (ou try/except) protègent les opérations sensibles."
            : "• Conseil : ajoutez un bloc try/catch autour des appels d'entrée/sortie ou réseau pour sécuriser l'exécution.");

        if (longLines > 0)
            sb.AppendLine($"• Lisibilité : {longLines} ligne(s) dépassent 120 caractères.");

        if (hasTodos)
            sb.AppendLine("• À noter : des marqueurs TODO/FIXME restent présents dans le fichier.");

        return sb.ToString().TrimEnd();
    }

    private const string _LocalHint = "http://localhost:11434";

    private static string? BuildSystemContext(AiCodeContext? context)
    {
        if (context is null)
            return null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.FilePath))
            parts.Add($"Fichier : {context.FilePath}");
        if (!string.IsNullOrWhiteSpace(context.Language))
            parts.Add($"Langage : {context.Language}");
        if (!string.IsNullOrWhiteSpace(context.SelectedCode))
            parts.Add($"Code :\n{context.SelectedCode}");

        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private static string? ExtractContent(JsonElement root)
    {
        // Format compatible OpenAI : choices[0].message.content
        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];
            if (first.TryGetProperty("message", out JsonElement choiceMessage)
                && choiceMessage.TryGetProperty("content", out JsonElement choiceContent)
                && choiceContent.ValueKind == JsonValueKind.String)
            {
                return choiceContent.GetString();
            }
        }

        // Format Ollama : message.content
        if (root.TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return null;
    }
}

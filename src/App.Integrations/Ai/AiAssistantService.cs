using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Ai;

public sealed record AiAssistantOptions(
    Uri Endpoint,
    string Model,
    string? ApiKey = null);

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly AiAssistantOptions _options;

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

        var messages = new List<object>();

        string? contextText = BuildSystemContext(request.Context);
        if (contextText is not null)
            messages.Add(new { role = "system", content = contextText });

        foreach (AiChatMessage message in request.Messages)
        {
            messages.Add(new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = message.Content
            });
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages,
                stream = false
            })
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
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
                throw new AppException(
                    ErrorCode.Unavailable,
                    $"Le service IA a retourné une erreur ({(int)response.StatusCode}).");
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
                        : _options.Model;

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

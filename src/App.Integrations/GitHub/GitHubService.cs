using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.GitHub;

public sealed class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;
    private readonly string? _accessToken;
    private readonly Uri _baseUri;

    public GitHubService(
        HttpClient httpClient,
        string? accessToken = null,
        Uri? baseUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessToken = accessToken;
        _baseUri = baseUri ?? new Uri("https://api.github.com/");
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(
        string owner,
        string repository,
        string state = "open",
        CancellationToken ct = default)
    {
        RequireNonEmpty(owner, "Le propriétaire du dépôt est obligatoire.");
        RequireNonEmpty(repository, "Le nom du dépôt est obligatoire.");
        RequireNonEmpty(state, "L'état des Pull Requests est obligatoire.");

        string path =
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pulls?state={Uri.EscapeDataString(state)}";

        using JsonDocument doc = await SendJsonAsync(HttpMethod.Get, path, null, ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new AppException(ErrorCode.Unavailable, "Réponse GitHub inattendue.");

        var pullRequests = new List<GitHubPullRequest>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
            pullRequests.Add(ParsePullRequest(item));

        return pullRequests;
    }

    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        CreatePullRequestOptions options,
        CancellationToken ct = default)
    {
        if (options is null)
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Les options de création de la Pull Request sont obligatoires.");

        RequireNonEmpty(options.Owner, "Le propriétaire du dépôt est obligatoire.");
        RequireNonEmpty(options.Repository, "Le nom du dépôt est obligatoire.");
        RequireNonEmpty(options.Title, "Le titre de la Pull Request est obligatoire.");
        RequireNonEmpty(options.HeadBranch, "La branche source est obligatoire.");
        RequireNonEmpty(options.BaseBranch, "La branche cible est obligatoire.");

        string path =
            $"repos/{Uri.EscapeDataString(options.Owner)}/{Uri.EscapeDataString(options.Repository)}/pulls";

        using JsonDocument doc = await SendJsonAsync(
            HttpMethod.Post,
            path,
            new Dictionary<string, string>
            {
                ["title"] = options.Title,
                ["head"] = options.HeadBranch,
                ["base"] = options.BaseBranch,
                ["body"] = options.Body ?? string.Empty
            },
            ct);

        return ParsePullRequest(doc.RootElement);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var uri = new Uri(_baseUri.AbsoluteUri.TrimEnd('/') + "/" + relativePath);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Keyce-Code", "1.0"));

        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        if (payload is not null)
            request.Content = JsonContent.Create(payload);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AppException(
                ErrorCode.Unavailable,
                $"Impossible de joindre GitHub : {ex.Message}",
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AppException(
                ErrorCode.Timeout,
                "La requête GitHub a expiré.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(
                    MapStatusCode(response.StatusCode),
                    $"GitHub a retourné une erreur ({(int)response.StatusCode}).");
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                throw new AppException(
                    ErrorCode.Unavailable,
                    "La réponse JSON de GitHub est invalide.",
                    ex);
            }
        }
    }

    private static GitHubPullRequest ParsePullRequest(JsonElement element)
    {
        int number = element.GetProperty("number").GetInt32();
        string title = element.GetProperty("title").GetString() ?? string.Empty;
        string state = element.GetProperty("state").GetString() ?? string.Empty;
        string headBranch = element.GetProperty("head").GetProperty("ref").GetString() ?? string.Empty;
        string baseBranch = element.GetProperty("base").GetProperty("ref").GetString() ?? string.Empty;
        string htmlUrl = element.GetProperty("html_url").GetString() ?? string.Empty;

        return new GitHubPullRequest(number, title, state, headBranch, baseBranch, htmlUrl);
    }

    private static ErrorCode MapStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ErrorCode.AccessDenied,
        HttpStatusCode.NotFound => ErrorCode.NotFound,
        HttpStatusCode.UnprocessableEntity => ErrorCode.Conflict,
        _ => ErrorCode.Unavailable
    };

    private static void RequireNonEmpty(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AppException(ErrorCode.InvalidArgument, message);
    }
}

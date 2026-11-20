using System.Net;
using System.Text;
using System.Text.Json;
using App.Core.Errors;
using App.Core.Models;
using App.Integrations.Ai;
using App.Integrations.GitHub;

namespace App.Integration.Tests;

public sealed class AiAndGitHubServiceTests
{
    [Fact]
    public async Task ChatAsync_ShouldSendMessagesAndContextAndParseOpenAiResponse()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "model": "gpt-test",
                  "choices": [
                    { "message": { "role": "assistant", "content": "Voici l'explication." } }
                  ]
                }
                """));

        using var http = new HttpClient(handler);
        var service = new AiAssistantService(
            http,
            new AiAssistantOptions(new Uri("https://ai.local/v1/chat/completions"), "gpt-test", "secret-key"));

        var request = new AiChatRequest(
            [new AiChatMessage(AiChatRole.User, "Explique ce code")],
            new AiCodeContext("Program.cs", "csharp", "Console.WriteLine(42);"));

        AiChatResponse response = await service.ChatAsync(request);

        Assert.Equal("Voici l'explication.", response.Content);
        Assert.Equal("gpt-test", response.Model);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", handler.LastRequest?.Headers.Authorization?.Parameter);

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("gpt-test", sent.RootElement.GetProperty("model").GetString());
        JsonElement messages = sent.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Program.cs", messages[0].GetProperty("content").GetString());
        Assert.Contains("Console.WriteLine(42);", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ExplainCodeAsync_AndSuggestFixAsync_ShouldReturnAssistantContent()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "model": "llama3",
                  "message": { "role": "assistant", "content": "Utilise int.TryParse." }
                }
                """));

        using var http = new HttpClient(handler);
        var service = new AiAssistantService(
            http,
            new AiAssistantOptions(new Uri("http://localhost:11434/api/chat"), "llama3"));

        string explanation = await service.ExplainCodeAsync("var x = int.Parse(s);", "csharp");
        string fix = await service.SuggestFixAsync("var x = int.Parse(s);", "FormatException", "csharp");

        Assert.Equal("Utilise int.TryParse.", explanation);
        Assert.Equal("Utilise int.TryParse.", fix);
        Assert.Contains("FormatException", handler.LastRequestBody);
    }

    [Fact]
    public async Task ChatAsync_WithEmptyMessagesOrServerError_ShouldThrowAppException()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.InternalServerError, "{}"));

        using var http = new HttpClient(handler);
        var service = new AiAssistantService(
            http,
            new AiAssistantOptions(new Uri("https://ai.local/chat"), "model"));

        AppException emptyEx = await Assert.ThrowsAsync<AppException>(
            () => service.ChatAsync(new AiChatRequest([])));
        Assert.Equal(ErrorCode.InvalidArgument, emptyEx.Code);

        AppException serverEx = await Assert.ThrowsAsync<AppException>(
            () => service.ChatAsync(new AiChatRequest([new AiChatMessage(AiChatRole.User, "Hi")])));
        Assert.Equal(ErrorCode.Unavailable, serverEx.Code);
    }

    [Fact]
    public async Task GetPullRequestsAsync_ShouldReturnParsedPullRequests()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                [
                  {
                    "number": 16,
                    "title": "feat(extensions): add API",
                    "state": "open",
                    "head": { "ref": "feature/extension-api" },
                    "base": { "ref": "main" },
                    "html_url": "https://github.com/floflowilwil-cpu/Keyce-code/pull/16"
                  }
                ]
                """));

        using var http = new HttpClient(handler);
        var service = new GitHubService(http, accessToken: "gh-token");

        IReadOnlyList<GitHubPullRequest> prs =
            await service.GetPullRequestsAsync("floflowilwil-cpu", "Keyce-code");

        GitHubPullRequest pr = Assert.Single(prs);
        Assert.Equal(16, pr.Number);
        Assert.Equal("feat(extensions): add API", pr.Title);
        Assert.Equal("open", pr.State);
        Assert.Equal("feature/extension-api", pr.HeadBranch);
        Assert.Equal("main", pr.BaseBranch);
        Assert.Equal(
            "https://api.github.com/repos/floflowilwil-cpu/Keyce-code/pulls?state=open",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("gh-token", handler.LastRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CreatePullRequestAsync_ShouldPostPayloadAndReturnCreatedPr()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(
                HttpStatusCode.Created,
                """
                {
                  "number": 17,
                  "title": "feat(git): add advanced git",
                  "state": "open",
                  "head": { "ref": "feature/git-advanced" },
                  "base": { "ref": "main" },
                  "html_url": "https://github.com/floflowilwil-cpu/Keyce-code/pull/17"
                }
                """));

        using var http = new HttpClient(handler);
        var service = new GitHubService(http, accessToken: "gh-token");

        GitHubPullRequest created = await service.CreatePullRequestAsync(
            new CreatePullRequestOptions(
                "floflowilwil-cpu",
                "Keyce-code",
                "feat(git): add advanced git",
                "feature/git-advanced",
                "main",
                "Ajoute Init, Merge et AbortMerge."));

        Assert.Equal(17, created.Number);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("feat(git): add advanced git", sent.RootElement.GetProperty("title").GetString());
        Assert.Equal("feature/git-advanced", sent.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", sent.RootElement.GetProperty("base").GetString());
        Assert.Equal("Ajoute Init, Merge et AbortMerge.", sent.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenUnauthorized_ShouldThrowAccessDenied()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            JsonResponse(HttpStatusCode.Unauthorized, """{ "message": "Bad credentials" }"""));

        using var http = new HttpClient(handler);
        var service = new GitHubService(http);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => service.GetPullRequestsAsync("floflowilwil-cpu", "Keyce-code"));

        Assert.Equal(ErrorCode.AccessDenied, ex.Code);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;

            return responder(request, cancellationToken);
        }
    }
}

using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Intégration GitHub : consultation et création de Pull Requests.</summary>
public interface IGitHubService
{
    Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(
        string owner,
        string repository,
        string state = "open",
        CancellationToken ct = default);

    Task<GitHubPullRequest> CreatePullRequestAsync(
        CreatePullRequestOptions options,
        CancellationToken ct = default);
}

using App.Core.Models;

namespace App.Core.Contracts;

/// <summary>Git piloté via la CLI. Utilisé par UI et extensions.</summary>
public interface IGitService
{
    Task<GitStatus> GetStatusAsync(CancellationToken ct = default);
    Task<string> DiffAsync(string? path = null, CancellationToken ct = default);
    Task StageAsync(string path, CancellationToken ct = default);
    Task UnstageAsync(string path, CancellationToken ct = default);
    Task CommitAsync(string message, CancellationToken ct = default);
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken ct = default);
    Task CreateBranchAsync(string branch, bool checkout = true, CancellationToken ct = default);
    Task CheckoutAsync(string branch, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(int maxCount = 50, CancellationToken ct = default);
    Task PushAsync(CancellationToken ct = default);
    Task PullAsync(CancellationToken ct = default);
}

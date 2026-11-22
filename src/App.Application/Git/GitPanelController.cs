using System.ComponentModel;
using System.Runtime.CompilerServices;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Application.Git;

/// <summary>État et actions du panneau Git (Source Control).</summary>
public sealed class GitPanelController : INotifyPropertyChanged
{
    private readonly IGitService _gitService;

    private string _currentBranch = string.Empty;
    private IReadOnlyList<GitFileStatus> _stagedFiles = [];
    private IReadOnlyList<GitFileStatus> _unstagedFiles = [];
    private IReadOnlyList<GitBranch> _branches = [];
    private IReadOnlyList<GitCommitInfo> _recentCommits = [];
    private string _commitMessage = string.Empty;
    private string _newBranchName = string.Empty;
    private IReadOnlyList<string> _conflictedFiles = [];
    private string _currentDiff = string.Empty;
    private string? _errorMessage;
    private string? _lastSuccessMessage;
    private bool _isBusy;

    public GitPanelController(IGitService gitService)
    {
        _gitService = gitService ?? throw new ArgumentNullException(nameof(gitService));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentBranch
    {
        get => _currentBranch;
        private set => SetProperty(ref _currentBranch, value);
    }

    public IReadOnlyList<GitFileStatus> StagedFiles
    {
        get => _stagedFiles;
        private set => SetProperty(ref _stagedFiles, value);
    }

    public IReadOnlyList<GitFileStatus> UnstagedFiles
    {
        get => _unstagedFiles;
        private set => SetProperty(ref _unstagedFiles, value);
    }

    public IReadOnlyList<GitBranch> Branches
    {
        get => _branches;
        private set => SetProperty(ref _branches, value);
    }

    public IReadOnlyList<GitCommitInfo> RecentCommits
    {
        get => _recentCommits;
        private set => SetProperty(ref _recentCommits, value);
    }

    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    public string NewBranchName
    {
        get => _newBranchName;
        set => SetProperty(ref _newBranchName, value);
    }

    public IReadOnlyList<string> ConflictedFiles
    {
        get => _conflictedFiles;
        private set
        {
            if (SetProperty(ref _conflictedFiles, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasConflicts)));
        }
    }

    public bool HasConflicts => _conflictedFiles.Count > 0;

    public string CurrentDiff
    {
        get => _currentDiff;
        private set => SetProperty(ref _currentDiff, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? LastSuccessMessage
    {
        get => _lastSuccessMessage;
        private set => SetProperty(ref _lastSuccessMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public Task<bool> RefreshAsync(CancellationToken ct = default) =>
        ExecuteSafeAsync(ReloadStateAsync, ct);

    public Task<bool> StageAsync(string path, CancellationToken ct = default) =>
        RunAndRefreshAsync(token => _gitService.StageAsync(path, token), $"✓ Fichier indexé : {path}", ct);

    public Task<bool> UnstageAsync(string path, CancellationToken ct = default) =>
        RunAndRefreshAsync(token => _gitService.UnstageAsync(path, token), $"✓ Retiré de l'index : {path}", ct);

    public Task<bool> StageAllAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(
            token => _gitService.StageAsync(".", token),
            "✓ Toutes les modifications ont été indexées.",
            ct);

    public Task<bool> CommitAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(async token =>
        {
            string message = CommitMessage;
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new AppException(
                    ErrorCode.InvalidArgument,
                    "Le message du commit ne peut pas être vide.");
            }

            GitStatus status = await _gitService.GetStatusAsync(token);
            bool hasStaged = status.Files.Any(f => f.IsStaged);
            if (!hasStaged)
            {
                if (status.Files.Count == 0)
                {
                    throw new AppException(
                        ErrorCode.InvalidArgument,
                        "Aucune modification à valider dans le dépôt.");
                }

                // Comme VS Code : si rien n'est indexé mais que des fichiers sont modifiés, indexer tout avant de valider.
                await _gitService.StageAsync(".", token);
            }

            await _gitService.CommitAsync(message, token);
            CommitMessage = string.Empty;
        }, "✓ Commit enregistré.", ct);

    public Task<bool> CheckoutBranchAsync(string branch, CancellationToken ct = default) =>
        RunAndRefreshAsync(token => _gitService.CheckoutAsync(branch, token), $"✓ Basculé sur la branche « {branch} ».", ct);

    public Task<bool> CreateBranchAsync(
        string branch,
        bool checkout = true,
        CancellationToken ct = default) =>
        RunAndRefreshAsync(token => _gitService.CreateBranchAsync(branch, checkout, token), $"✓ Branche « {branch} » créée.", ct);

    public Task<bool> CreateNewBranchAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(async token =>
        {
            string name = NewBranchName.Trim();
            await _gitService.CreateBranchAsync(name, checkout: true, token);
            NewBranchName = string.Empty;
        }, "✓ Nouvelle branche créée et activée.", ct);

    public Task<bool> MergeBranchAsync(string branch, CancellationToken ct = default) =>
        ExecuteSafeAsync(async token =>
        {
            GitMergeResult result = await _gitService.MergeAsync(branch, token);
            await ReloadStateAsync(token);
            if (!result.Succeeded)
                ErrorMessage = $"Conflit de fusion sur : {string.Join(", ", result.ConflictedFiles)}";
            else
                LastSuccessMessage = $"✓ Branche « {branch} » fusionnée.";
        }, ct);

    public Task<bool> AbortMergeAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(_gitService.AbortMergeAsync, "✓ Fusion annulée (Abort Merge).", ct);

    public Task<bool> LoadDiffAsync(string? path = null, CancellationToken ct = default) =>
        ExecuteSafeAsync(async token =>
        {
            CurrentDiff = await _gitService.DiffAsync(path, token);
        }, ct);

    public Task<bool> PushAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(_gitService.PushAsync, "✓ Push effectué vers le dépôt distant.", ct);

    public Task<bool> PullAsync(CancellationToken ct = default) =>
        RunAndRefreshAsync(_gitService.PullAsync, "✓ Pull effectué depuis le dépôt distant.", ct);

    private Task<bool> RunAndRefreshAsync(
        Func<CancellationToken, Task> operation,
        string successMessage,
        CancellationToken ct) =>
        ExecuteSafeAsync(async token =>
        {
            await operation(token);
            await ReloadStateAsync(token);
            LastSuccessMessage = successMessage;
        }, ct);

    private async Task ReloadStateAsync(CancellationToken ct)
    {
        GitStatus status = await _gitService.GetStatusAsync(ct);
        CurrentBranch = status.Branch;
        StagedFiles = status.Files.Where(f => f.IsStaged).ToList();
        UnstagedFiles = status.Files.Where(f => !f.IsStaged).ToList();
        ConflictedFiles = status.Files
            .Where(f => f.State == GitFileState.Conflicted)
            .Select(f => f.Path)
            .ToList();

        Branches = await _gitService.GetBranchesAsync(ct);

        try
        {
            RecentCommits = await _gitService.GetLogAsync(20, ct);
        }
        catch (AppException)
        {
            // Dépôt initialisé sans commit : aucun historique disponible pour l'instant.
            RecentCommits = [];
        }
    }

    private async Task<bool> ExecuteSafeAsync(
        Func<CancellationToken, Task> action,
        CancellationToken ct)
    {
        IsBusy = true;
        ErrorMessage = null;
        LastSuccessMessage = null;

        try
        {
            await action(ct);
            return true;
        }
        catch (AppException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

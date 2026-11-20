
using System.Diagnostics;
using System.Globalization;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Git;

public sealed class GitService : IGitService
{
    private readonly IWorkspaceService _workspaceService;

    public GitService(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService
            ?? throw new ArgumentNullException(nameof(workspaceService));
    }

    public async Task InitAsync(
        string initialBranch = "main",
        CancellationToken ct = default)
    {
        RequireNonEmpty(initialBranch, "Le nom de la branche initiale ne peut pas être vide.");
        await RunGitAsync(["init", "-b", initialBranch], ct);
    }

    public async Task<GitStatus> GetStatusAsync(
        CancellationToken ct = default)
    {
        string output = await RunGitAsync(
            ["status", "--porcelain=v1", "-z", "--branch"],
            ct);

        string[] entries = output.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries);

        string branch = string.Empty;
        var files = new List<GitFileStatus>();

        int startIndex = 0;

        if (entries.Length > 0)
        {
            string header = entries[0].TrimEnd('\r', '\n');

            if (header.StartsWith("## "))
            {
                branch = header[3..];

                int separator = branch.IndexOf("...");
                if (separator >= 0)
                    branch = branch[..separator];

                if (branch.StartsWith("No commits yet on "))
                {
                    branch = branch["No commits yet on ".Length..];
                }

                startIndex = 1;
            }
        }

        for (int i = startIndex; i < entries.Length; i++)
        {
            string entry = entries[i].TrimStart('\r', '\n');

            if (entry.Length < 4)
                continue;

            string status = entry[..2];
            string path = entry[3..];

            // En mode -z, un renommage ou une copie est suivi
            // d'un second champ contenant l'ancien chemin.
            if ((status.Contains('R') || status.Contains('C')) &&
                i + 1 < entries.Length)
            {
                i++;
            }

            files.Add(new GitFileStatus(
                path,
                ParseFileState(status),
                status[0] != ' ' && status[0] != '?'));
        }

        return new GitStatus(branch, files);
    }

    public Task<string> DiffAsync(
        string? path = null,
        CancellationToken ct = default)
    {
        // Compare le contenu actuel au dernier commit :
        // inclut les modifications indexées et non indexées.
        var arguments = new List<string> { "diff", "HEAD" };

        if (!string.IsNullOrWhiteSpace(path))
        {
            arguments.Add("--");
            arguments.Add(path);
        }

        return RunGitAsync(arguments, ct);
    }

    public async Task StageAsync(
        string path,
        CancellationToken ct = default)
    {
        RequireNonEmpty(path, "Le chemin du fichier ne peut pas être vide.");
        await RunGitAsync(["add", "--", path], ct);
    }

    public async Task UnstageAsync(
        string path,
        CancellationToken ct = default)
    {
        RequireNonEmpty(path, "Le chemin du fichier ne peut pas être vide.");
        await RunGitAsync(["restore", "--staged", "--", path], ct);
    }

    public async Task CommitAsync(
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le message du commit ne peut pas être vide.");
        }

        await RunGitAsync(["commit", "-m", message], ct);
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
        CancellationToken ct = default)
    {
        string output = await RunGitAsync(
            ["branch", "--format=%(HEAD)|%(refname:short)"],
            ct);

        var branches = new List<GitBranch>();

        foreach (string line in output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimEnd('\r');
            int separator = trimmed.IndexOf('|');
            if (separator < 0)
                continue;

            bool isCurrent = trimmed[..separator].Trim() == "*";
            string name = trimmed[(separator + 1)..].Trim();

            if (!string.IsNullOrEmpty(name))
                branches.Add(new GitBranch(name, isCurrent));
        }

        return branches;
    }

    public async Task CreateBranchAsync(
        string branch,
        bool checkout = true,
        CancellationToken ct = default)
    {
        RequireNonEmpty(branch, "Le nom de la branche ne peut pas être vide.");

        if (checkout)
            await RunGitAsync(["switch", "-c", branch], ct);
        else
            await RunGitAsync(["branch", "--", branch], ct);
    }

    public async Task CheckoutAsync(
        string branch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le nom de la branche ne peut pas être vide.");
        }

        await RunGitAsync(["switch", "--", branch], ct);
    }

    public async Task<GitMergeResult> MergeAsync(
        string branch,
        CancellationToken ct = default)
    {
        RequireNonEmpty(branch, "Le nom de la branche à fusionner ne peut pas être vide.");

        try
        {
            await RunGitAsync(["merge", "--no-edit", "--", branch], ct);
            return new GitMergeResult(true, []);
        }
        catch (AppException)
        {
            IReadOnlyList<string> conflicts = await GetConflictsAsync(ct);
            if (conflicts.Count > 0)
                return new GitMergeResult(false, conflicts);

            throw;
        }
    }

    public async Task AbortMergeAsync(CancellationToken ct = default)
    {
        await RunGitAsync(["merge", "--abort"], ct);
    }

    public async Task<IReadOnlyList<string>> GetConflictsAsync(
        CancellationToken ct = default)
    {
        GitStatus status = await GetStatusAsync(ct);
        return status.Files
            .Where(f => f.State == GitFileState.Conflicted)
            .Select(f => f.Path)
            .ToList();
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(
        int maxCount = 50,
        CancellationToken ct = default)
    {
        if (maxCount <= 0)
        {
            throw new AppException(
                ErrorCode.InvalidArgument,
                "Le nombre maximal de commits doit être supérieur à zéro.");
        }

        string output = await RunGitAsync(
            ["log", $"-n{maxCount}", "--format=%H%x1f%an%x1f%aI%x1f%s%x1e"],
            ct);

        var commits = new List<GitCommitInfo>();

        foreach (string entry in output.Split(
            '\u001e',
            StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim('\r', '\n');
            if (string.IsNullOrEmpty(trimmed))
                continue;

            string[] parts = trimmed.Split('\u001f');
            if (parts.Length < 4)
                continue;

            if (!DateTimeOffset.TryParse(
                parts[2],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsedDate))
            {
                parsedDate = DateTimeOffset.MinValue;
            }

            commits.Add(new GitCommitInfo(
                parts[0],
                parts[1],
                parsedDate,
                parts[3]));
        }

        return commits;
    }

    public async Task PushAsync(CancellationToken ct = default)
    {
        await RunGitAsync(["push"], ct);
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        await RunGitAsync(["pull"], ct);
    }

    private static void RequireNonEmpty(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AppException(ErrorCode.InvalidArgument, message);
    }

    private async Task<string> RunGitAsync(
        IEnumerable<string> arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string workingDirectory = GetWorkspaceRoot();

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new AppException(
                    ErrorCode.Unavailable,
                    "Impossible de démarrer le processus Git.");
            }

            Task<string> outputTask =
                process.StandardOutput.ReadToEndAsync(ct);

            Task<string> errorTask =
                process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new AppException(
                    ErrorCode.Conflict,
                    string.IsNullOrWhiteSpace(error)
                        ? $"La commande Git a échoué (code {process.ExitCode})."
                        : error.Trim());
            }

            return output.Replace("\r\n", "\n");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (InvalidOperationException)
                {
                    // Le processus est déjà terminé.
                }
            }

            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AppException(
                ErrorCode.Unavailable,
                "Git est introuvable ou ne peut pas être exécuté.",
                ex);
        }
    }

    private string GetWorkspaceRoot()
    {
        WorkspaceInfo? workspace = _workspaceService.Current;

        if (workspace is null)
        {
            throw new AppException(
                ErrorCode.Unavailable,
                "Aucun espace de travail n'est actuellement ouvert.");
        }

        if (!Directory.Exists(workspace.RootPath))
        {
            throw new AppException(
                ErrorCode.NotFound,
                $"Le dossier du workspace est introuvable : {workspace.RootPath}");
        }

        return workspace.RootPath;
    }

    private static GitFileState ParseFileState(string status)
    {
        if (status == "??")
            return GitFileState.Untracked;

        if (status.Contains('U') ||
            status == "AA" ||
            status == "DD")
            return GitFileState.Conflicted;

        if (status.Contains('R'))
            return GitFileState.Renamed;

        if (status.Contains('D'))
            return GitFileState.Deleted;

        if (status.Contains('A'))
            return GitFileState.Added;

        return GitFileState.Modified;
    }
}

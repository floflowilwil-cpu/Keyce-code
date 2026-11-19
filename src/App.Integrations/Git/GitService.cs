
using System.Diagnostics;
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

    public async Task<GitStatus> GetStatusAsync(
        CancellationToken ct = default)
    {
        string output = await RunGitAsync(
            ["status", "--porcelain=v1", "--branch"],
            ct);

        string branch = string.Empty;
        var files = new List<GitFileStatus>();

        foreach (string line in output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("## "))
            {
                branch = line[3..].Trim();

                int separator = branch.IndexOf("...");
                if (separator >= 0)
                    branch = branch[..separator];

                if (branch.StartsWith("No commits yet on "))
                    branch = branch["No commits yet on ".Length..];

                continue;
            }

            if (line.Length < 4)
                continue;

            string status = line[..2];
            string path = line[3..].Trim();

            if (path.Contains(" -> "))
            {
                path = path[
                    (path.LastIndexOf(" -> ", StringComparison.Ordinal) + 4)..];
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
        var arguments = new List<string> { "diff" };

        if (!string.IsNullOrWhiteSpace(path))
        {
            arguments.Add("--");
            arguments.Add(path);
        }

        return RunGitAsync(arguments, ct);
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

    public async Task PushAsync(CancellationToken ct = default)
    {
        await RunGitAsync(["push"], ct);
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        await RunGitAsync(["pull"], ct);
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


using System.Diagnostics;
using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using App.Integrations.Git;

namespace App.Integration.Tests;

public class GitServiceTests
{
    [Fact]
    public async Task GetStatusAsync_ShouldReturnBranchAndFileStates()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "tracked.txt"),
                "modified content");

            await File.WriteAllTextAsync(
                Path.Combine(directory, "new.txt"),
                "new content");

            var service = CreateService(directory);

            GitStatus status = await service.GetStatusAsync();

            Assert.Equal("main", status.Branch);

            GitFileStatus modified = Assert.Single(
                status.Files,
                file => file.Path == "tracked.txt");

            Assert.Equal(GitFileState.Modified, modified.State);
            Assert.False(modified.IsStaged);

            GitFileStatus untracked = Assert.Single(
                status.Files,
                file => file.Path == "new.txt");

            Assert.Equal(GitFileState.Untracked, untracked.State);
            Assert.False(untracked.IsStaged);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task GetStatusAsync_ShouldPreserveLeadingSpacesInFileNames()
    {
        string directory = CreateTemporaryDirectory();
        const string fileName = " spaced file.txt";

        try
        {
            await InitializeRepositoryAsync(directory);

            await File.WriteAllTextAsync(
                Path.Combine(directory, fileName),
                "content");

            var service = CreateService(directory);

            GitStatus status = await service.GetStatusAsync();

            GitFileStatus file = Assert.Single(
                status.Files,
                file => file.Path == fileName);

            Assert.Equal(GitFileState.Untracked, file.State);
            Assert.False(file.IsStaged);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task GetStatusAsync_ShouldDetectRenamedFiles()
    {
        string directory = CreateTemporaryDirectory();

        const string oldFileName = "tracked.txt";
        const string newFileName = "renamed.txt";

        try
        {
            await InitializeRepositoryAsync(directory);

            string oldPath = Path.Combine(directory, oldFileName);
            string newPath = Path.Combine(directory, newFileName);

            File.Move(oldPath, newPath);

            await RunGitAsync(directory, "add", "-A");

            var service = CreateService(directory);

            GitStatus status = await service.GetStatusAsync();

            GitFileStatus renamed = Assert.Single(
                status.Files,
                file => file.Path == newFileName);

            Assert.Equal(GitFileState.Renamed, renamed.State);
            Assert.True(renamed.IsStaged);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task GetStatusAsync_WithCanceledToken_ShouldThrowOperationCanceledException()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            var service = CreateService(directory);

            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetStatusAsync(cancellationTokenSource.Token));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DiffAsync_ShouldReturnFileChanges()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "tracked.txt"),
                "modified content");

            var service = CreateService(directory);

            string diff = await service.DiffAsync("tracked.txt");

            Assert.Contains("-initial content", diff);
            Assert.Contains("+modified content", diff);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DiffAsync_ShouldIncludeStagedAndUnstagedChanges()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            string filePath = Path.Combine(directory, "tracked.txt");

            await File.WriteAllTextAsync(filePath, "staged content");
            await RunGitAsync(directory, "add", "tracked.txt");

            await File.WriteAllTextAsync(filePath, "working tree content");

            var service = CreateService(directory);

            string diff = await service.DiffAsync("tracked.txt");

            Assert.Contains("-initial content", diff);
            Assert.Contains("+working tree content", diff);
            Assert.DoesNotContain("staged content", diff);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsync_ShouldCreateCommit()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            await File.WriteAllTextAsync(
                Path.Combine(directory, "tracked.txt"),
                "committed content");

            await RunGitAsync(directory, "add", "tracked.txt");

            var service = CreateService(directory);

            await service.CommitAsync("Test commit");

            string message = await RunGitAsync(
                directory,
                "log",
                "-1",
                "--format=%s");

            Assert.Equal("Test commit", message.Trim());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CheckoutAsync_ShouldSwitchBranch()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            await RunGitAsync(directory, "branch", "test-branch");

            var service = CreateService(directory);

            await service.CheckoutAsync("test-branch");

            string branch = await RunGitAsync(
                directory,
                "branch",
                "--show-current");

            Assert.Equal("test-branch", branch.Trim());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CheckoutAsync_WithUnknownBranch_ShouldThrowAppException()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            var service = CreateService(directory);

            AppException exception = await Assert.ThrowsAsync<AppException>(
                () => service.CheckoutAsync("branch-that-does-not-exist"));

            Assert.Equal(ErrorCode.Conflict, exception.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task PushAsync_ShouldPushCommitToRemoteRepository()
    {
        string directory = CreateTemporaryDirectory();
        string remoteDirectory = Path.Combine(directory, "remote.git");
        string repositoryDirectory = Path.Combine(directory, "repository");

        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            await RunGitAsync(
                directory,
                "init",
                "--bare",
                "--initial-branch=main",
                remoteDirectory);

            await InitializeRepositoryAsync(repositoryDirectory);

            await RunGitAsync(
                repositoryDirectory,
                "remote",
                "add",
                "origin",
                remoteDirectory);

            await RunGitAsync(
                repositoryDirectory,
                "push",
                "-u",
                "origin",
                "main");

            await File.WriteAllTextAsync(
                Path.Combine(repositoryDirectory, "pushed.txt"),
                "pushed content");

            await RunGitAsync(repositoryDirectory, "add", "pushed.txt");
            await RunGitAsync(
                repositoryDirectory,
                "commit",
                "-m",
                "Push test commit");

            var service = CreateService(repositoryDirectory);

            await service.PushAsync();

            string remoteMessage = await RunGitAsync(
                remoteDirectory,
                "log",
                "-1",
                "--format=%s",
                "refs/heads/main");

            Assert.Equal("Push test commit", remoteMessage.Trim());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task PullAsync_ShouldRetrieveRemoteChanges()
    {
        string directory = CreateTemporaryDirectory();
        string remoteDirectory = Path.Combine(directory, "remote.git");
        string repositoryDirectory = Path.Combine(directory, "repository");
        string cloneDirectory = Path.Combine(directory, "clone");

        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            await RunGitAsync(
                directory,
                "init",
                "--bare",
                "--initial-branch=main",
                remoteDirectory);

            await InitializeRepositoryAsync(repositoryDirectory);

            await RunGitAsync(
                repositoryDirectory,
                "remote",
                "add",
                "origin",
                remoteDirectory);

            await RunGitAsync(
                repositoryDirectory,
                "push",
                "-u",
                "origin",
                "main");

            await RunGitAsync(
                directory,
                "clone",
                remoteDirectory,
                cloneDirectory);

            await RunGitAsync(cloneDirectory, "config", "user.name", "Git Tests");
            await RunGitAsync(
                cloneDirectory,
                "config",
                "user.email",
                "git-tests@example.com");

            await File.WriteAllTextAsync(
                Path.Combine(cloneDirectory, "tracked.txt"),
                "updated from remote");

            await RunGitAsync(cloneDirectory, "add", "tracked.txt");
            await RunGitAsync(
                cloneDirectory,
                "commit",
                "-m",
                "Remote update");

            await RunGitAsync(cloneDirectory, "push");

            var service = CreateService(repositoryDirectory);

            await service.PullAsync();

            string content = await File.ReadAllTextAsync(
                Path.Combine(repositoryDirectory, "tracked.txt"));

            Assert.Equal("updated from remote", content);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task GetStatusAsync_WithoutWorkspace_ShouldThrowAppException()
    {
        var workspace = new FakeWorkspaceService(null);
        var service = new GitService(workspace);

        AppException exception = await Assert.ThrowsAsync<AppException>(
            () => service.GetStatusAsync());

        Assert.Equal(ErrorCode.Unavailable, exception.Code);
    }

    [Fact]
    public async Task CommitAsync_WithEmptyMessage_ShouldThrowAppException()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);

            var service = CreateService(directory);

            AppException exception = await Assert.ThrowsAsync<AppException>(
                () => service.CommitAsync(" "));

            Assert.Equal(ErrorCode.InvalidArgument, exception.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static GitService CreateService(string directory)
    {
        var workspace = new FakeWorkspaceService(
            new WorkspaceInfo(directory, Path.GetFileName(directory)));

        return new GitService(workspace);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"GitServiceTest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static async Task InitializeRepositoryAsync(string directory)
    {
        await RunGitAsync(directory, "init", "-b", "main");
        await RunGitAsync(directory, "config", "user.name", "Git Tests");
        await RunGitAsync(
            directory,
            "config",
            "user.email",
            "git-tests@example.com");

        await File.WriteAllTextAsync(
            Path.Combine(directory, "tracked.txt"),
            "initial content");

        await RunGitAsync(directory, "add", "tracked.txt");
        await RunGitAsync(directory, "commit", "-m", "Initial commit");
    }

    private static async Task<string> RunGitAsync(
        string directory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Impossible de démarrer Git pour les tests.");

        Task<string> outputTask =
            process.StandardOutput.ReadToEndAsync();

        Task<string> errorTask =
            process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git a échoué : {error}");
        }

        return output;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(
            directory,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (string subdirectory in Directory.EnumerateDirectories(
            directory,
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(subdirectory, FileAttributes.Normal);
        }

        File.SetAttributes(directory, FileAttributes.Normal);

        Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeWorkspaceService(
        WorkspaceInfo? current) : IWorkspaceService
    {
        public WorkspaceInfo? Current { get; private set; } = current;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

        public Task OpenWorkspaceAsync(
            string rootPath,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            WorkspaceInfo? previous = Current;

            Current = new WorkspaceInfo(
                rootPath,
                Path.GetFileName(rootPath));

            WorkspaceChanged?.Invoke(
                this,
                new WorkspaceChangedEventArgs(previous, Current));

            return Task.CompletedTask;
        }

        public Task CloseWorkspaceAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            WorkspaceInfo? previous = Current;
            Current = null;

            WorkspaceChanged?.Invoke(
                this,
                new WorkspaceChangedEventArgs(previous, null));

            return Task.CompletedTask;
        }
    }
}

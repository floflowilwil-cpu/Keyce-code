using System.Diagnostics;
using App.Application.Git;
using App.Core.Contracts;
using App.Core.Models;
using App.Integrations.Git;

namespace App.Integration.Tests;

public sealed class GitPanelControllerTests
{
    [Fact]
    public async Task RefreshAsync_ShouldPopulateBranchStagedUnstagedAndCommits()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "staged");
            await RunGitAsync(directory, "add", "tracked.txt");
            await File.WriteAllTextAsync(Path.Combine(directory, "new.txt"), "untracked");

            GitPanelController controller = CreateController(directory);

            bool ok = await controller.RefreshAsync();

            Assert.True(ok);
            Assert.False(controller.IsBusy);
            Assert.Null(controller.ErrorMessage);
            Assert.Equal("main", controller.CurrentBranch);
            Assert.Equal("tracked.txt", Assert.Single(controller.StagedFiles).Path);
            Assert.Equal("new.txt", Assert.Single(controller.UnstagedFiles).Path);
            Assert.Equal("main", Assert.Single(controller.Branches).Name);
            Assert.Equal("Initial commit", Assert.Single(controller.RecentCommits).Message);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task StageAsync_AndUnstageAsync_ShouldMoveFileBetweenLists()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "modified");

            GitPanelController controller = CreateController(directory);
            await controller.RefreshAsync();

            Assert.Empty(controller.StagedFiles);
            Assert.Single(controller.UnstagedFiles);

            Assert.True(await controller.StageAsync("tracked.txt"));
            Assert.Equal("tracked.txt", Assert.Single(controller.StagedFiles).Path);
            Assert.Empty(controller.UnstagedFiles);

            Assert.True(await controller.UnstageAsync("tracked.txt"));
            Assert.Empty(controller.StagedFiles);
            Assert.Equal("tracked.txt", Assert.Single(controller.UnstagedFiles).Path);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsync_ShouldCreateCommitClearMessageAndRefreshLists()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "committed");

            GitPanelController controller = CreateController(directory);
            await controller.StageAsync("tracked.txt");

            controller.CommitMessage = "Commit from panel";
            Assert.True(await controller.CommitAsync());

            Assert.Equal(string.Empty, controller.CommitMessage);
            Assert.Empty(controller.StagedFiles);
            Assert.Empty(controller.UnstagedFiles);
            Assert.Equal(2, controller.RecentCommits.Count);
            Assert.Equal("Commit from panel", controller.RecentCommits[0].Message);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CreateBranchAsync_AndCheckoutBranchAsync_ShouldUpdateCurrentBranch()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);
            GitPanelController controller = CreateController(directory);

            Assert.True(await controller.CreateBranchAsync("feature/panel", checkout: true));
            Assert.Equal("feature/panel", controller.CurrentBranch);

            Assert.True(await controller.CheckoutBranchAsync("main"));
            Assert.Equal("main", controller.CurrentBranch);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsync_WithEmptyMessage_ShouldSetErrorMessageWithoutThrowing()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await InitializeRepositoryAsync(directory);
            GitPanelController controller = CreateController(directory);
            controller.CommitMessage = "   ";

            bool ok = await controller.CommitAsync();

            Assert.False(ok);
            Assert.False(controller.IsBusy);
            Assert.False(string.IsNullOrWhiteSpace(controller.ErrorMessage));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static GitPanelController CreateController(string directory)
    {
        var workspace = new FakeWorkspaceService(
            new WorkspaceInfo(directory, Path.GetFileName(directory)));
        return new GitPanelController(new GitService(workspace));
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"GitPanelTest_{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task InitializeRepositoryAsync(string directory)
    {
        await RunGitAsync(directory, "init", "-b", "main");
        await RunGitAsync(directory, "config", "user.name", "Git Tests");
        await RunGitAsync(directory, "config", "user.email", "git-tests@example.com");

        await File.WriteAllTextAsync(Path.Combine(directory, "tracked.txt"), "initial content");
        await RunGitAsync(directory, "add", "tracked.txt");
        await RunGitAsync(directory, "commit", "-m", "Initial commit");
    }

    private static async Task<string> RunGitAsync(string directory, params string[] arguments)
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
            ?? throw new InvalidOperationException("Impossible de démarrer Git.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git a échoué : {error}");

        return output;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        foreach (string dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(dir, FileAttributes.Normal);

        File.SetAttributes(directory, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeWorkspaceService(WorkspaceInfo? current) : IWorkspaceService
    {
        public WorkspaceInfo? Current { get; } = current;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged
        {
            add { }
            remove { }
        }

        public Task OpenWorkspaceAsync(string rootPath, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CloseWorkspaceAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

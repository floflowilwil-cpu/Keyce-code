using App.Core.Contracts;
using App.Core.Errors;
using App.Core.Models;
using App.Extensions.Tests.Demo;

namespace App.Extensions.Tests;

/// <summary>Vérifie l'isolation et le contrôle des permissions des extensions.</summary>
public sealed class ExtensionPermissionsTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "keyce-perm-tests-" + Guid.NewGuid().ToString("N"));

    private string ExtensionsRoot => Path.Combine(_temp, "installed");

    public ExtensionPermissionsTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private string CreateCodeExtension(string id, Type entryType, string permissionsJson)
    {
        string folder = Path.Combine(_temp, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        string assemblyPath = entryType.Assembly.Location;
        string assemblyFile = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(folder, assemblyFile));

        File.WriteAllText(
            Path.Combine(folder, "extension.json"),
            $$"""
            {
              "id": "{{id}}",
              "name": "Demo {{id}}",
              "version": "1.0.0",
              "description": "Test permissions",
              "permissions": [{{permissionsJson}}],
              "main": "{{assemblyFile}}",
              "entryType": "{{entryType.FullName}}"
            }
            """);

        return folder;
    }

    [Fact]
    public async Task Install_UnknownPermission_ThrowsInvalidArgument()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        string source = CreateCodeExtension("hello", typeof(HelloWorldExtension), "\"admin.root\"");

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(source));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Activate_RegisterCommandWithoutPermission_ThrowsAccessDenied()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        string source = CreateCodeExtension("hello", typeof(HelloWorldExtension), "");
        await manager.InstallAsync(source);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("hello"));

        Assert.Equal(ErrorCode.AccessDenied, ex.Code);
        Assert.Equal(ExtensionState.Installed, Assert.Single(manager.Extensions).State);
    }

    [Fact]
    public async Task Activate_CommandWithForeignPrefix_ThrowsAccessDenied()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        // HelloWorldExtension enregistre "hello.sayHello", mais l'id ici est "autre".
        string source = CreateCodeExtension("autre", typeof(HelloWorldExtension), "\"commands\"");
        await manager.InstallAsync(source);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("autre"));

        Assert.Equal(ErrorCode.AccessDenied, ex.Code);
    }

    [Fact]
    public async Task Activate_AccessGitWithoutPermission_ThrowsAccessDenied()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        string source = CreateCodeExtension("gitext", typeof(GitProbeExtension), "\"commands\"");
        await manager.InstallAsync(source);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("gitext"));

        Assert.Equal(ErrorCode.AccessDenied, ex.Code);
    }

    [Fact]
    public async Task Activate_AccessGitWithPermission_Succeeds()
    {
        var manager = new ExtensionManager(ExtensionsRoot, git: new FakeGitService());
        string source = CreateCodeExtension("gitext", typeof(GitProbeExtension), "\"git\"");
        await manager.InstallAsync(source);

        await manager.ActivateAsync("gitext");

        Assert.Equal(ExtensionState.Active, Assert.Single(manager.Extensions).State);
    }

    public sealed class GitProbeExtension : IExtension
    {
        public Task ActivateAsync(IExtensionContext context, CancellationToken ct = default)
        {
            _ = context.Git;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeGitService : IGitService
    {
        public Task<GitStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new GitStatus("main", []));
        public Task<string> DiffAsync(string? path = null, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
        public Task StageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnstageAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitBranch>>([]);
        public Task CreateBranchAsync(string branch, bool checkout = true, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task CheckoutAsync(string branch, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(int maxCount = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitCommitInfo>>([]);
        public Task PushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PullAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

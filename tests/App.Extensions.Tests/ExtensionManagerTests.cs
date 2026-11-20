using App.Core.Errors;
using App.Core.Models;

namespace App.Extensions.Tests;

public sealed class ExtensionManagerTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "keyce-ext-tests-" + Guid.NewGuid().ToString("N"));

    private string ExtensionsRoot => Path.Combine(_temp, "installed");

    public ExtensionManagerTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private ExtensionManager CreateManager() => new(ExtensionsRoot);

    private string CreateSource(string id)
    {
        string folder = Path.Combine(_temp, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(folder, "extension.json"),
            $$"""{ "id": "{{id}}", "name": "Test", "version": "1.0.0", "description": "d", "permissions": ["commands"] }""");
        File.WriteAllText(Path.Combine(folder, "main.txt"), "contenu");

        return folder;
    }

    [Fact]
    public async Task Install_CopiesFilesAndRegistersExtension()
    {
        var manager = CreateManager();

        ExtensionInfo info = await manager.InstallAsync(CreateSource("hello"));

        Assert.Equal("hello", info.Manifest.Id);
        Assert.Equal(ExtensionState.Installed, info.State);
        Assert.True(File.Exists(Path.Combine(info.InstallPath, "main.txt")));
        Assert.Single(manager.Extensions);
    }

    [Fact]
    public async Task Install_SameExtensionTwice_ThrowsAlreadyExists()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreateSource("hello"));

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(CreateSource("hello")));

        Assert.Equal(ErrorCode.AlreadyExists, ex.Code);
    }

    [Fact]
    public async Task Install_InvalidId_ThrowsInvalidArgument()
    {
        var manager = CreateManager();

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(CreateSource("../evil")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Install_MissingManifest_ThrowsNotFound()
    {
        var manager = CreateManager();
        string emptyFolder = Path.Combine(_temp, "empty");
        Directory.CreateDirectory(emptyFolder);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(emptyFolder));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Activate_ChangesStateAndRaisesEvent()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreateSource("hello"));

        ExtensionChangedEventArgs? received = null;
        manager.ExtensionChanged += (_, e) => received = e;

        await manager.ActivateAsync("hello");

        Assert.Equal(ExtensionState.Active, Assert.Single(manager.Extensions).State);
        Assert.NotNull(received);
        Assert.Equal(ExtensionChangeKind.Activated, received!.Kind);
    }

    [Fact]
    public async Task Deactivate_ReturnsToInstalledState()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreateSource("hello"));
        await manager.ActivateAsync("hello");

        await manager.DeactivateAsync("hello");

        Assert.Equal(ExtensionState.Installed, Assert.Single(manager.Extensions).State);
    }

    [Fact]
    public async Task ActiveState_IsKeptAfterReload()
    {
        var first = CreateManager();
        await first.InstallAsync(CreateSource("hello"));
        await first.ActivateAsync("hello");

        var second = CreateManager();
        await second.LoadInstalledAsync();

        ExtensionInfo info = Assert.Single(second.Extensions);
        Assert.Equal("hello", info.Manifest.Id);
        Assert.Equal(ExtensionState.Active, info.State);
    }

    [Fact]
    public async Task Uninstall_RemovesExtensionAndFolder()
    {
        var manager = CreateManager();
        ExtensionInfo info = await manager.InstallAsync(CreateSource("hello"));

        await manager.UninstallAsync("hello");

        Assert.Empty(manager.Extensions);
        Assert.False(Directory.Exists(info.InstallPath));
    }

    [Fact]
    public async Task Activate_UnknownExtension_ThrowsNotFound()
    {
        var manager = CreateManager();

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("inconnue"));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }
}

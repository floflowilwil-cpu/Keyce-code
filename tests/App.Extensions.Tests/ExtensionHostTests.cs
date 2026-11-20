using App.Core.Errors;
using App.Core.Models;
using App.Extensions.Tests.Demo;

namespace App.Extensions.Tests;

/// <summary>Tests de bout en bout : une vraie .dll est installée, chargée puis activée.</summary>
public sealed class ExtensionHostTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "keyce-host-tests-" + Guid.NewGuid().ToString("N"));

    private string ExtensionsRoot => Path.Combine(_temp, "installed");

    public ExtensionHostTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private string CreateCodeExtension(string id, Type entryType, string permissions = "\"commands\"")
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
              "description": "Extension de test",
              "permissions": [{{permissions}}],
              "main": "{{assemblyFile}}",
              "entryType": "{{entryType.FullName}}"
            }
            """);

        return folder;
    }

    [Fact]
    public async Task Activate_DemoExtension_RegistersCommand()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        await manager.InstallAsync(CreateCodeExtension("hello", typeof(HelloWorldExtension)));

        await manager.ActivateAsync("hello");

        CommandInfo command = Assert.Single(manager.Commands.Commands);
        Assert.Equal(HelloWorldExtension.CommandId, command.Id);
        Assert.Equal("hello", command.OwnerId);
    }

    [Fact]
    public async Task ExecuteCommand_RunsExtensionCode()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        ExtensionInfo info = await manager.InstallAsync(
            CreateCodeExtension("hello", typeof(HelloWorldExtension)));
        await manager.ActivateAsync("hello");

        await manager.Commands.ExecuteAsync(HelloWorldExtension.CommandId);

        string text = await File.ReadAllTextAsync(Path.Combine(info.InstallPath, "hello.txt"));
        Assert.Equal("Bonjour depuis Demo hello !", text);
    }

    [Fact]
    public async Task Deactivate_RemovesExtensionCommands()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        await manager.InstallAsync(CreateCodeExtension("hello", typeof(HelloWorldExtension)));
        await manager.ActivateAsync("hello");

        await manager.DeactivateAsync("hello");

        Assert.Empty(manager.Commands.Commands);
        Assert.Equal(ExtensionState.Installed, Assert.Single(manager.Extensions).State);
    }

    [Fact]
    public async Task Uninstall_ActiveExtension_RemovesCommandsAndFolder()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        ExtensionInfo info = await manager.InstallAsync(
            CreateCodeExtension("hello", typeof(HelloWorldExtension)));
        await manager.ActivateAsync("hello");

        await manager.UninstallAsync("hello");

        Assert.Empty(manager.Commands.Commands);
        Assert.False(Directory.Exists(info.InstallPath));
    }

    [Fact]
    public async Task Activate_CrashingExtension_ThrowsAndStaysInstalled()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        await manager.InstallAsync(CreateCodeExtension("crash", typeof(CrashingExtension)));

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("crash"));

        Assert.Equal(ErrorCode.Unknown, ex.Code);
        Assert.Equal(ExtensionState.Installed, Assert.Single(manager.Extensions).State);
        Assert.Empty(manager.Commands.Commands);
    }

    [Fact]
    public async Task Activate_MissingMainFile_ThrowsNotFound()
    {
        var manager = new ExtensionManager(ExtensionsRoot);
        string source = CreateCodeExtension("hello", typeof(HelloWorldExtension));
        File.Delete(Path.Combine(source, Path.GetFileName(typeof(HelloWorldExtension).Assembly.Location)));
        await manager.InstallAsync(source);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("hello"));

        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task LoadInstalled_RestartsActiveExtensions()
    {
        var first = new ExtensionManager(ExtensionsRoot);
        await first.InstallAsync(CreateCodeExtension("hello", typeof(HelloWorldExtension)));
        await first.ActivateAsync("hello");
        await first.DeactivateAsync("hello");
        await first.ActivateAsync("hello");

        var second = new ExtensionManager(ExtensionsRoot);
        await second.LoadInstalledAsync();

        Assert.Equal(HelloWorldExtension.CommandId, Assert.Single(second.Commands.Commands).Id);
    }
}

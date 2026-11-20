using System.Text.Json;
using App.Core.Errors;
using App.Core.Models;
using App.Extensions.Catalog;
using App.Extensions.Tests.Demo;

namespace App.Extensions.Tests;

public sealed class ExtensionCatalogTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "keyce-catalog-tests-" + Guid.NewGuid().ToString("N"));

    private string ExtensionsRoot => Path.Combine(_temp, "installed");
    private string CatalogRoot => Path.Combine(_temp, "catalog");

    public ExtensionCatalogTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private ExtensionManager CreateManager(Version? appVersion = null) =>
        new(ExtensionsRoot, appVersion: appVersion);

    private string CreatePackage(
        string parentDir,
        string folderName,
        string id,
        string version,
        string description = "Extension de test",
        string? minAppVersion = null,
        string[]? dependencies = null,
        Type? entryType = null,
        string? markerContent = null)
    {
        string folder = Path.Combine(parentDir, folderName);
        Directory.CreateDirectory(folder);

        string? mainFile = null;
        if (entryType is not null)
        {
            string assemblyPath = entryType.Assembly.Location;
            mainFile = Path.GetFileName(assemblyPath);
            File.Copy(assemblyPath, Path.Combine(folder, mainFile), overwrite: true);
        }

        string json = JsonSerializer.Serialize(new
        {
            id,
            name = $"Demo {id}",
            version,
            description,
            permissions = entryType is not null ? new[] { "commands" } : Array.Empty<string>(),
            main = mainFile,
            entryType = entryType?.FullName,
            minAppVersion,
            dependencies
        });

        File.WriteAllText(Path.Combine(folder, "extension.json"), json);

        if (markerContent is not null)
            File.WriteAllText(Path.Combine(folder, "marker.txt"), markerContent);

        return folder;
    }

    [Fact]
    public async Task Install_IncompatibleMinAppVersion_ThrowsConflict()
    {
        var manager = CreateManager(new Version(1, 0, 0));
        string source = CreatePackage(
            _temp, "src-compat", "future", "1.0.0", minAppVersion: "2.0.0");

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(source));

        Assert.Equal(ErrorCode.Conflict, ex.Code);
        Assert.Empty(manager.Extensions);
    }

    [Fact]
    public async Task Install_MissingDependency_ThrowsConflict()
    {
        var manager = CreateManager();
        string source = CreatePackage(
            _temp, "src-dep", "plugin", "1.0.0", dependencies: ["core-lib"]);

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.InstallAsync(source));

        Assert.Equal(ErrorCode.Conflict, ex.Code);
        Assert.Empty(manager.Extensions);
    }

    [Fact]
    public async Task Activate_InactiveDependency_ThrowsConflict_ThenSucceedsWhenActive()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreatePackage(_temp, "src-base", "base", "1.0.0"));
        await manager.InstallAsync(CreatePackage(
            _temp, "src-child", "child", "1.0.0", dependencies: ["base"]));

        AppException ex = await Assert.ThrowsAsync<AppException>(
            () => manager.ActivateAsync("child"));

        Assert.Equal(ErrorCode.Conflict, ex.Code);

        await manager.ActivateAsync("base");
        await manager.ActivateAsync("child");

        Assert.All(manager.Extensions, e => Assert.Equal(ExtensionState.Active, e.State));
    }

    [Fact]
    public async Task Uninstall_AndDeactivate_WhenRequiredByDependentExtension_ThrowsConflict()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreatePackage(_temp, "src-base", "base", "1.0.0"));
        await manager.InstallAsync(CreatePackage(
            _temp, "src-child", "child", "1.0.0", dependencies: ["base"]));
        await manager.ActivateAsync("base");
        await manager.ActivateAsync("child");

        AppException deactivateEx = await Assert.ThrowsAsync<AppException>(
            () => manager.DeactivateAsync("base"));
        Assert.Equal(ErrorCode.Conflict, deactivateEx.Code);

        await manager.DeactivateAsync("child");

        AppException uninstallEx = await Assert.ThrowsAsync<AppException>(
            () => manager.UninstallAsync("base"));
        Assert.Equal(ErrorCode.Conflict, uninstallEx.Code);
    }

    [Fact]
    public async Task Update_NewerVersion_ReplacesFilesAndPreservesActiveState()
    {
        var manager = CreateManager();
        string v1 = CreatePackage(
            _temp, "src-v1", "hello", "1.0.0",
            entryType: typeof(HelloWorldExtension), markerContent: "v1");
        string v2 = CreatePackage(
            _temp, "src-v2", "hello", "1.1.0",
            entryType: typeof(HelloWorldExtension), markerContent: "v2");

        await manager.InstallAsync(v1);
        await manager.ActivateAsync("hello");

        var events = new List<ExtensionChangeKind>();
        manager.ExtensionChanged += (_, e) => events.Add(e.Kind);

        ExtensionInfo updated = await manager.UpdateAsync(v2);

        Assert.Equal("1.1.0", updated.Manifest.Version);
        Assert.Equal(ExtensionState.Active, updated.State);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(updated.InstallPath, "marker.txt")));
        Assert.Equal(HelloWorldExtension.CommandId, Assert.Single(manager.Commands.Commands).Id);
        Assert.Contains(ExtensionChangeKind.Updated, events);
    }

    [Fact]
    public async Task Update_SameOrOlderVersion_ThrowsConflict()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreatePackage(_temp, "src-v2", "hello", "2.0.0"));
        string same = CreatePackage(_temp, "src-same", "hello", "2.0.0");
        string older = CreatePackage(_temp, "src-old", "hello", "1.5.0");

        AppException sameEx = await Assert.ThrowsAsync<AppException>(
            () => manager.UpdateAsync(same));
        Assert.Equal(ErrorCode.Conflict, sameEx.Code);

        AppException olderEx = await Assert.ThrowsAsync<AppException>(
            () => manager.UpdateAsync(older));
        Assert.Equal(ErrorCode.Conflict, olderEx.Code);
    }

    [Fact]
    public async Task Catalog_SearchAndCheckUpdates_ReturnsMatchesAndAvailableUpdates()
    {
        var manager = CreateManager();
        await manager.InstallAsync(CreatePackage(_temp, "installed-a", "alpha", "1.0.0"));

        Directory.CreateDirectory(CatalogRoot);
        CreatePackage(CatalogRoot, "pkg-alpha", "alpha", "1.2.0", description: "Formatage C#");
        CreatePackage(CatalogRoot, "pkg-beta", "beta", "1.0.0", description: "Thème sombre");

        var catalog = new ExtensionCatalogService(CatalogRoot, manager);

        IReadOnlyList<ExtensionCatalogEntry> all = await catalog.SearchAsync();
        Assert.Equal(2, all.Count);

        IReadOnlyList<ExtensionCatalogEntry> filtered = await catalog.SearchAsync("formatage");
        Assert.Equal("alpha", Assert.Single(filtered).Manifest.Id);

        IReadOnlyList<ExtensionUpdateInfo> updates = await catalog.CheckUpdatesAsync();
        ExtensionUpdateInfo update = Assert.Single(updates);
        Assert.Equal("alpha", update.ExtensionId);
        Assert.Equal("1.0.0", update.InstalledVersion);
        Assert.Equal("1.2.0", update.AvailableVersion);
    }

    [Fact]
    public async Task Catalog_InstallAndUpdateFromCatalog_WorksEndToEnd()
    {
        var manager = CreateManager();
        Directory.CreateDirectory(CatalogRoot);
        CreatePackage(CatalogRoot, "pkg-alpha", "alpha", "1.0.0", markerContent: "v1");

        var catalog = new ExtensionCatalogService(CatalogRoot, manager);

        ExtensionInfo installed = await catalog.InstallFromCatalogAsync("alpha");
        Assert.Equal("1.0.0", installed.Manifest.Version);

        CreatePackage(CatalogRoot, "pkg-alpha", "alpha", "2.0.0", markerContent: "v2");

        ExtensionInfo updated = await catalog.UpdateFromCatalogAsync("alpha");
        Assert.Equal("2.0.0", updated.Manifest.Version);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(updated.InstallPath, "marker.txt")));
        Assert.Empty(await catalog.CheckUpdatesAsync());
    }
}

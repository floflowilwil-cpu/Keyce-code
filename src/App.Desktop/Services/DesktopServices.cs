using System;
using System.IO;
using App.Application.Git;
using App.Application.Workspaces;
using App.Core.Contracts;
using App.Editor;
using App.Editor.Editing;
using App.Extensions;
using App.Extensions.Catalog;
using App.Extensions.Commands;
using App.Integrations.Debug;
using App.Integrations.Files;
using App.Integrations.Git;
using App.Integrations.Terminal;
using App.Languages;

namespace App.Desktop.Services;

/// <summary>
/// Composition root de l'application Desktop : construit les services concrets
/// une seule fois et partage les mêmes instances entre les ViewModels.
/// </summary>
public sealed class DesktopServices : IDisposable
{
    public DesktopServices()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyceCode");
        Directory.CreateDirectory(appData);

        Files = new FileService();
        Workspace = new WorkspaceService();
        Editor = new EditorService(Files);
        EditorCommands = new EditorCommands(Editor);

        Git = new GitService(Workspace);
        GitPanel = new GitPanelController(Git);

        Terminal = new TerminalService();
        Debug = new DebugService();
        Languages = new LanguageClient();

        Commands = new CommandService();
        Extensions = new ExtensionManager(
            Path.Combine(appData, "Extensions"),
            Commands,
            Git,
            Workspace);
        ExtensionCatalog = new ExtensionCatalogService(
            Path.Combine(appData, "ExtensionCatalog"),
            Extensions);
    }

    public IFileService Files { get; }
    public WorkspaceService Workspace { get; }
    public EditorService Editor { get; }
    public IEditorCommands EditorCommands { get; }
    public IGitService Git { get; }
    public GitPanelController GitPanel { get; }
    public ITerminalService Terminal { get; }
    public IDebugService Debug { get; }
    public ILanguageClient Languages { get; }
    public ICommandService Commands { get; }
    public IExtensionService Extensions { get; }
    public IExtensionCatalogService ExtensionCatalog { get; }

    public void Dispose()
    {
        Editor.Dispose();
        if (Languages is IDisposable disposableLanguages)
            disposableLanguages.Dispose();
    }
}

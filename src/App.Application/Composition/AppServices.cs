using App.Application.Sync;
using App.Application.Workspaces;
using App.Core.Contracts;
using App.Extensions;
using App.Extensions.Commands;
using App.Languages;

namespace App.Application.Composition;

/// <summary>
/// Racine de composition : le SEUL endroit qui connaît les implémentations concrètes.
/// L'interface (App.Desktop) ne reçoit que des contrats ; elle ne fait jamais « new GitService() ».
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly LanguageClient _languageClient;
    private readonly LanguageSyncService _languageSync;
    private readonly App.Editor.EditorService _editorService;

    private AppServices(
        IWorkspaceService workspace,
        IEncodedFileService files,
        App.Editor.EditorService editor,
        IEditorCommands editorCommands,
        IGitService git,
        ITerminalService terminal,
        IDebugService debugger,
        LanguageClient languages,
        LanguageSyncService languageSync,
        ICommandService commandRegistry,
        ExtensionManager extensions)
    {
        Workspace = workspace;
        Files = files;
        _editorService = editor;
        Editor = editor;
        EditorCommands = editorCommands;
        Git = git;
        Terminal = terminal;
        Debugger = debugger;
        _languageClient = languages;
        Languages = languages;
        _languageSync = languageSync;
        CommandRegistry = commandRegistry;
        Extensions = extensions;
    }

    public IWorkspaceService Workspace { get; }
    public IEncodedFileService Files { get; }
    public IEditorService Editor { get; }
    public IEditorCommands EditorCommands { get; }
    public IGitService Git { get; }
    public ITerminalService Terminal { get; }
    public IDebugService Debugger { get; }
    public ILanguageClient Languages { get; }
    public ICommandService CommandRegistry { get; }
    public ExtensionManager Extensions { get; }

    /// <summary>Messages non bloquants du LSP (à afficher dans l'onglet Sortie).</summary>
    public event EventHandler<string>? LanguageMessage
    {
        add => _languageSync.Message += value;
        remove => _languageSync.Message -= value;
    }

    public static AppServices Create()
    {
        var workspace = new WorkspaceService();
        var files = new App.Integrations.Files.FileService();
        var editor = new App.Editor.EditorService(files);
        var editorCommands = new App.Editor.Editing.EditorCommands(editor);
        var git = new App.Integrations.Git.GitService(workspace);
        var terminal = new App.Integrations.Terminal.TerminalService();
        var debugger = new App.Integrations.Debug.DebugService();
        var languages = new LanguageClient();
        var languageSync = new LanguageSyncService(editor, languages, workspace);
        var commandRegistry = new CommandService();

        string extensionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KeyceCode", "extensions");
        var extensions = new ExtensionManager(extensionsRoot, commandRegistry, git, workspace);

        return new AppServices(
            workspace, files, editor, editorCommands, git, terminal, debugger,
            languages, languageSync, commandRegistry, extensions);
    }

    public void Dispose()
    {
        _languageSync.Dispose();
        _languageClient.Dispose();
        _editorService.Dispose();
    }
}

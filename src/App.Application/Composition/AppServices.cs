using App.Application.Sync;
using App.Application.Workspaces;
using App.Core.Contracts;
using App.Extensions;
using App.Extensions.Catalog;
using App.Extensions.Commands;
using App.Integrations.Ai;
using App.Integrations.Execution;
using App.Integrations.GitHub;
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
    private readonly HttpClient _httpClient;

    private AppServices(
        IWorkspaceService workspace,
        IEncodedFileService files,
        App.Editor.EditorService editor,
        IEditorCommands editorCommands,
        IGitService git,
        ITerminalService terminal,
        IExecutionService execution,
        IDebugService debugger,
        LanguageClient languages,
        LanguageSyncService languageSync,
        ICommandService commandRegistry,
        ExtensionManager extensions,
        IExtensionCatalogService extensionCatalog,
        IGitHubService gitHub,
        IAiAssistantService aiAssistant,
        HttpClient httpClient)
    {
        Workspace = workspace;
        Files = files;
        _editorService = editor;
        Editor = editor;
        EditorCommands = editorCommands;
        Git = git;
        Terminal = terminal;
        Execution = execution;
        Debugger = debugger;
        _languageClient = languages;
        Languages = languages;
        _languageSync = languageSync;
        CommandRegistry = commandRegistry;
        Extensions = extensions;
        ExtensionCatalog = extensionCatalog;
        GitHub = gitHub;
        AiAssistant = aiAssistant;
        _httpClient = httpClient;
    }

    public IWorkspaceService Workspace { get; }
    public IEncodedFileService Files { get; }
    public IEditorService Editor { get; }
    public IEditorCommands EditorCommands { get; }
    public IGitService Git { get; }
    public ITerminalService Terminal { get; }
    public IExecutionService Execution { get; }
    public IDebugService Debugger { get; }
    public ILanguageClient Languages { get; }
    public ICommandService CommandRegistry { get; }
    public ExtensionManager Extensions { get; }
    public IExtensionCatalogService ExtensionCatalog { get; }
    public IGitHubService GitHub { get; }
    public IAiAssistantService AiAssistant { get; }

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
        var execution = new ExecutionService();
        var debugger = new App.Integrations.Debug.DebugService();
        var languages = new LanguageClient();
        var languageSync = new LanguageSyncService(editor, languages, workspace);
        var commandRegistry = new CommandService();

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KeyceCode");
        var extensions = new ExtensionManager(
            Path.Combine(appData, "extensions"), commandRegistry, git, workspace);
        var extensionCatalog = new ExtensionCatalogService(
            Path.Combine(appData, "catalog"), extensions);

        var httpClient = new HttpClient();
        var gitHub = new GitHubService(
            httpClient,
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        string? envAiUrl = Environment.GetEnvironmentVariable("KEYCE_AI_ENDPOINT");
        string aiUrl = string.IsNullOrWhiteSpace(envAiUrl)
            ? "http://localhost:11434/api/chat"
            : envAiUrl;
        string? envAiModel = Environment.GetEnvironmentVariable("KEYCE_AI_MODEL");
        string aiModel = string.IsNullOrWhiteSpace(envAiModel) ? "llama3" : envAiModel;
        string? aiKey = Environment.GetEnvironmentVariable("KEYCE_AI_KEY");
        var aiAssistant = new AiAssistantService(
            httpClient,
            new AiAssistantOptions(new Uri(aiUrl), aiModel, aiKey));

        return new AppServices(
            workspace, files, editor, editorCommands, git, terminal, execution, debugger,
            languages, languageSync, commandRegistry, extensions, extensionCatalog,
            gitHub, aiAssistant, httpClient);
    }

    public void Dispose()
    {
        _languageSync.Dispose();
        _languageClient.Dispose();
        _editorService.Dispose();
        _httpClient.Dispose();
    }
}

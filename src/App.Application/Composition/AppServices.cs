using App.Application.Sync;
using App.Application.Workspaces;
using App.Core.Contracts;
using App.Extensions;
using App.Extensions.BuiltIn;
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
    private readonly SyntaxHighlighter _highlighter;
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
        IRunPlanner runPlanner,
        ITaskService tasks,
        LanguageClient languages,
        LanguageSyncService languageSync,
        SyntaxHighlighter highlighter,
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
        RunPlanner = runPlanner;
        Tasks = tasks;
        _languageClient = languages;
        Languages = languages;
        _languageSync = languageSync;
        _highlighter = highlighter;
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
    public IRunPlanner RunPlanner { get; }
    public ITaskService Tasks { get; }
    public ILanguageClient Languages { get; }

    /// <summary>Coloration syntaxique (suit seule les documents de l'éditeur ; à passer au contrôle d'édition).</summary>
    public ISyntaxHighlighter Highlighter => _highlighter;
    public ISnippetProvider Snippets { get; } = new SnippetProvider();
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
        var runPlanner = new RunPlanner();
        var tasks = new App.Integrations.Tasks.TaskService();
        var languages = new LanguageClient();
        var languageSync = new LanguageSyncService(editor, languages, workspace);
        var highlighter = new SyntaxHighlighter(editor);
        var commandRegistry = new CommandService();

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KeyceCode");
        string catalogRoot = Path.Combine(appData, "catalog");
        string extensionsRoot = Path.Combine(appData, "extensions");
        LegacyDemoCleaner.Clean(catalogRoot, extensionsRoot);

        var extensions = new ExtensionManager(
            extensionsRoot, commandRegistry, git, workspace);

        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KeyceCode/1.0");

        var extensionCatalog = new ExtensionCatalogService(catalogRoot, extensions, httpClient);
        var gitHub = new GitHubService(
            httpClient,
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        string? envAiUrl = Environment.GetEnvironmentVariable("KEYCE_AI_ENDPOINT");
        string? envAiModel = Environment.GetEnvironmentVariable("KEYCE_AI_MODEL");
        string? aiKey = Environment.GetEnvironmentVariable("KEYCE_AI_KEY");

        Uri aiEndpoint = new(string.IsNullOrWhiteSpace(envAiUrl)
            ? "https://text.pollinations.ai/openai"
            : envAiUrl);
        string aiModel = string.IsNullOrWhiteSpace(envAiModel) ? "openai" : envAiModel;

        var aiAssistant = new AiAssistantService(
            httpClient,
            new AiAssistantOptions(
                aiEndpoint,
                aiModel,
                aiKey,
                AllowOfflineFallback: false,
                FallbackProviders: null));

        return new AppServices(
            workspace, files, editor, editorCommands, git, terminal, execution, debugger, runPlanner, tasks,
            languages, languageSync, highlighter, commandRegistry, extensions, extensionCatalog,
            gitHub, aiAssistant, httpClient);
    }

    public string ExtractCodeBlock(string response) =>
        AiAssistantService.ExtractCodeBlock(response);

    public Task InitializeExtensionsAsync(CancellationToken ct = default) =>
        Extensions.LoadInstalledAsync(ct);

    public void Dispose()
    {
        _highlighter.Dispose();
        _languageSync.Dispose();
        _languageClient.Dispose();
        _editorService.Dispose();
        _httpClient.Dispose();
    }
}

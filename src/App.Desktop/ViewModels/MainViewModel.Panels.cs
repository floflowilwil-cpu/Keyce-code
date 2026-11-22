using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using App.Core.Errors;
using App.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.Desktop.ViewModels;

public sealed record LocalProjectItem(
    string Name,
    string Category,
    string FullPath,
    bool IsGitRepo);

// Panneaux « Contrôle de code source » (état), « Exécuter et déboguer » et « Extensions ».
public partial class MainWindowViewModel
{
    /// <summary>Appelée par le constructeur : abonne les panneaux aux événements des services.</summary>
    private void WirePanels()
    {
        Git.Controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "ErrorMessage") UI(UpdateGitState);
            else if (e.PropertyName is "StagedFiles" or "UnstagedFiles") UI(UpdateGitCounts);
        };
        _s.Execution.OutputReceived += (_, e) => UI(() => OnExecutionOutputReceived(e));
        _s.Extensions.ExtensionChanged += (_, _) => UI(() => _ = RefreshExtensionsAsync());
        _s.CommandRegistry.CommandsChanged += (_, _) => UI(RefreshExtensionCommands);
        _s.ExtensionCatalog.DownloadProgressChanged += (_, p) => UI(() =>
        {
            IsCatalogInstalling = true;
            CatalogInstallProgress = p.Percentage;
            CatalogProgressText = p.StatusText;
            ExtensionsInfo = p.StatusText;
            StatusMessage = p.StatusText;
        });
    }

    /// <summary>À appeler une fois au démarrage (charge les extensions installées et ouvre le dépôt Git courant).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await _s.InitializeExtensionsAsync();
        }
        catch (Exception ex)
        {
            ExtensionsInfo = "Erreur extensions : " + ex.Message;
            Log("Extensions : " + ex.Message);
        }

        await RefreshExtensionsAsync(updateSummary: true);

        if (!HasFolder
            && !string.IsNullOrWhiteSpace(DetectedGitRepoPath)
            && Directory.Exists(DetectedGitRepoPath))
        {
            try
            {
                await OpenWorkspaceAsync(DetectedGitRepoPath);
            }
            catch (Exception ex)
            {
                Log("Dépôt auto : " + ex.Message);
            }
        }

        DiscoverLocalProjects();
    }

    // =====================================================================
    //  Panneau latéral droit : Assistant IA & Git directement visibles
    // =====================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRightAiHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsRightGitHighlighted))]
    private bool _isRightPanelVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRightAiOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsRightGitOnlyMode))]
    [NotifyPropertyChangedFor(nameof(IsRightAiVisible))]
    [NotifyPropertyChangedFor(nameof(IsRightGitVisible))]
    [NotifyPropertyChangedFor(nameof(IsRightAiHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsRightGitHighlighted))]
    private int _rightPanelTab = 1;

    public bool IsRightAiOnlyMode => RightPanelTab != 2;
    public bool IsRightGitOnlyMode => RightPanelTab == 2;
    public bool IsRightAiVisible => RightPanelTab != 2;
    public bool IsRightGitVisible => RightPanelTab == 2;
    public bool IsRightAiHighlighted => IsRightPanelVisible && IsRightAiVisible;
    public bool IsRightGitHighlighted => IsRightPanelVisible && IsRightGitVisible;

    [RelayCommand]
    private void SetRightPanelTab(string? tab)
    {
        if (!int.TryParse(tab, out int mode) || mode is not (1 or 2))
            return;

        RightPanelTab = mode;
        IsRightPanelVisible = true;

        if (mode == 2)
            _ = Git.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void ToggleRightPanel() =>
        IsRightPanelVisible = !IsRightPanelVisible;

    [RelayCommand]
    private void FocusRightAi()
    {
        if (IsRightPanelVisible && RightPanelTab == 1)
        {
            IsRightPanelVisible = false;
            return;
        }

        RightPanelTab = 1;
        IsRightPanelVisible = true;
    }

    [RelayCommand]
    private void FocusRightGit()
    {
        if (IsRightPanelVisible && RightPanelTab == 2)
        {
            IsRightPanelVisible = false;
            return;
        }

        RightPanelTab = 2;
        IsRightPanelVisible = true;
        _ = Git.RefreshCommand.ExecuteAsync(null);
    }

    // =====================================================================
    //  Git : état du dépôt
    // =====================================================================

    [ObservableProperty] private bool _isGitRepoMissing;
    [ObservableProperty] private bool _hasGitError;
    [ObservableProperty] private string _gitMessage = "";

    [ObservableProperty] private int _stagedCount;
    [ObservableProperty] private int _unstagedCount;

    public ObservableCollection<GitHubPullRequest> PullRequests { get; } = new();
    public ObservableCollection<LocalProjectItem> LocalProjects { get; } = new();
    [ObservableProperty] private bool _isProjectsListVisible;
    [ObservableProperty] private string _projectsSummary = "Autres projets sur ce PC";
    [ObservableProperty] private string _gitHubOwner = "floflowilwil-cpu";
    [ObservableProperty] private string _gitHubRepo = "Keyce-code";
    [ObservableProperty] private string _newPrTitle = "";
    [ObservableProperty] private string _newPrBase = "main";
    [ObservableProperty] private string _gitHubStatus = "PRs GitHub en ligne (nécessite Internet vers github.com).";

    public string? DetectedGitRepoPath { get; } = FindGitRoot(Environment.CurrentDirectory);
    public bool HasDetectedGitRepo => DetectedGitRepoPath is not null;

    [RelayCommand]
    private void ToggleProjectsList()
    {
        DiscoverLocalProjects();
        IsProjectsListVisible = !IsProjectsListVisible;
    }

    [RelayCommand]
    private async Task OpenLocalProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        IsProjectsListVisible = false;
        await OpenWorkspaceAsync(path);
        DiscoverLocalProjects();
    }

    private void DiscoverLocalProjects()
    {
        string? anchorPath = _s.Workspace.Current?.RootPath ?? DetectedGitRepoPath ?? Environment.CurrentDirectory;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discovered = new List<LocalProjectItem>();

        if (!string.IsNullOrWhiteSpace(anchorPath))
            seen.Add(Path.GetFullPath(anchorPath));

        var searchRoots = new List<(string Path, int Depth)>();
        if (!string.IsNullOrWhiteSpace(anchorPath))
        {
            DirectoryInfo? parent = Directory.GetParent(anchorPath);
            if (parent is not null)
            {
                searchRoots.Add((parent.FullName, 1));
                if (parent.Parent is { } grandParent)
                    searchRoots.Add((grandParent.FullName, 2));
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            searchRoots.Add((Path.Combine(userProfile, "source", "repos"), 1));
            searchRoots.Add((Path.Combine(userProfile, "Projects"), 1));
        }

        foreach ((string root, int maxDepth) in searchRoots)
        {
            ScanProjectDirectory(root, maxDepth, seen, discovered);
            if (discovered.Count >= 30)
                break;
        }

        LocalProjects.Clear();
        foreach (LocalProjectItem item in discovered
                     .OrderByDescending(p => p.IsGitRepo)
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            LocalProjects.Add(item);
        }

        ProjectsSummary = LocalProjects.Count == 0
            ? "Aucun autre projet détecté automatiquement. Utilisez « Ouvrir un autre projet... »."
            : $"{LocalProjects.Count} autre(s) projet(s) trouvé(s) sur ce PC :";
    }

    private static void ScanProjectDirectory(
        string rootPath,
        int remainingDepth,
        HashSet<string> seen,
        List<LocalProjectItem> results)
    {
        if (remainingDepth <= 0 || results.Count >= 30 || !Directory.Exists(rootPath))
            return;

        string[] subDirs;
        try
        {
            subDirs = System.IO.Directory.GetDirectories(rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string parentName = Path.GetFileName(rootPath);
        foreach (string dir in subDirs)
        {
            if (results.Count >= 30)
                return;

            string fullPath = Path.GetFullPath(dir);
            string name = Path.GetFileName(fullPath);
            if (name.StartsWith('.') || Ignored.Contains(name))
                continue;

            bool isGit = Directory.Exists(Path.Combine(fullPath, ".git"));
            bool looksLikeProject = isGit || LooksLikeCodeProject(fullPath);

            if (looksLikeProject || remainingDepth == 1)
            {
                if (seen.Add(fullPath))
                {
                    string tag = isGit ? $"{parentName} • Dépôt Git" : parentName;
                    results.Add(new LocalProjectItem(name, tag, fullPath, isGit));
                }

                continue;
            }

            ScanProjectDirectory(fullPath, remainingDepth - 1, seen, results);
        }
    }

    private static bool LooksLikeCodeProject(string dirPath)
    {
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(dirPath))
            {
                string ext = Path.GetExtension(file);
                string fileName = Path.GetFileName(file);
                if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private void UpdateGitCounts()
    {
        StagedCount = Git.Controller.StagedFiles.Count;
        UnstagedCount = Git.Controller.UnstagedFiles.Count;
    }

    private void UpdateGitState()
    {
        string? msg = Git.Controller.ErrorMessage;
        WorkspaceInfo? ws = _s.Workspace.Current;
        bool hasWs = ws is not null;
        string? gitRoot = ws is not null ? FindGitRoot(ws.RootPath) : null;
        bool noGitRoot = hasWs && gitRoot is null;
        bool missing = noGitRoot
            || (!string.IsNullOrEmpty(msg)
                && (msg.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("pas un dépôt git", StringComparison.OrdinalIgnoreCase)));

        IsGitRepoMissing = missing;
        HasGitError = hasWs && !missing && !string.IsNullOrEmpty(msg);
        GitMessage = missing ? "Ce dossier n'est pas un dépôt Git." : msg ?? "";

        if (gitRoot is not null)
            SyncGitHubRepoFromGitConfig(gitRoot);
    }

    private void SyncGitHubRepoFromGitConfig(string gitRoot)
    {
        string configPath = Path.Combine(gitRoot, ".git", "config");
        if (!File.Exists(configPath))
            return;

        try
        {
            string[] lines = File.ReadAllLines(configPath);
            bool inOrigin = false;
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                    continue;

                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;

                string url = line[(eq + 1)..].Trim();
                if (TryParseGitHubRemote(url, out string owner, out string repo))
                {
                    GitHubOwner = owner;
                    GitHubRepo = repo;
                }

                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseGitHubRemote(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        const string githubHost = "github.com";
        int idx = url.IndexOf(githubHost, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        string tail = url[(idx + githubHost.Length)..].TrimStart(':', '/');
        if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            tail = tail[..^4];

        string[] parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        return owner.Length > 0 && repo.Length > 0;
    }

    private static string? FindGitRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    [RelayCommand]
    private async Task OpenDetectedGitRepo()
    {
        if (!string.IsNullOrWhiteSpace(DetectedGitRepoPath) && Directory.Exists(DetectedGitRepoPath))
            await OpenWorkspaceAsync(DetectedGitRepoPath);
    }

    [RelayCommand]
    private async Task InitGit()
    {
        try
        {
            await _s.Git.InitAsync();
            Log("Dépôt Git initialisé");
            await Git.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { ReportErrorMessage(ex); }
    }

    [RelayCommand]
    private async Task ViewFullDiff()
    {
        await Git.LoadDiffCommand.ExecuteAsync(null);
        string diff = Git.Controller.CurrentDiff;
        if (string.IsNullOrWhiteSpace(diff))
        {
            StatusMessage = "Aucune différence Git par rapport au dernier commit.";
            Log(StatusMessage);
            return;
        }

        Log($"Diff Git complet :\n{diff}");
        ShowOutput();
        StatusMessage = "Diff Git affiché dans l'onglet Sortie.";
    }

    [RelayCommand]
    private async Task LoadGitHubPullRequests()
    {
        string owner = GitHubOwner.Trim();
        string repo = GitHubRepo.Trim();
        if (owner.Length == 0 || repo.Length == 0)
        {
            GitHubStatus = "Indiquez Owner et Dépôt GitHub.";
            return;
        }

        try
        {
            GitHubStatus = $"Chargement des PRs de {owner}/{repo}…";
            IReadOnlyList<GitHubPullRequest> prs =
                await _s.GitHub.GetPullRequestsAsync(owner, repo, "open");
            PullRequests.Clear();
            foreach (GitHubPullRequest pr in prs)
                PullRequests.Add(pr);

            GitHubStatus = prs.Count == 0
                ? $"Aucune Pull Request ouverte sur {owner}/{repo}."
                : $"{prs.Count} Pull Request(s) ouverte(s) sur {owner}/{repo}.";
            StatusMessage = GitHubStatus;
            Log($"[GitHub] {GitHubStatus}");
        }
        catch (Exception ex)
        {
            GitHubStatus = FormatGitHubErrorMessage(ex);
            StatusMessage = GitHubStatus;
            Log("[GitHub] " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateGitHubPullRequest()
    {
        string owner = GitHubOwner.Trim();
        string repo = GitHubRepo.Trim();
        string title = NewPrTitle.Trim();
        string head = Git.Controller.CurrentBranch.Trim();
        string baseBranch = string.IsNullOrWhiteSpace(NewPrBase) ? "main" : NewPrBase.Trim();

        if (owner.Length == 0 || repo.Length == 0 || title.Length == 0)
        {
            GitHubStatus = "Indiquez Owner, Dépôt et Titre de la PR.";
            return;
        }

        if (string.IsNullOrWhiteSpace(head))
        {
            GitHubStatus = "Ouvrez d'abord un dépôt Git pour connaître la branche source.";
            return;
        }

        try
        {
            GitHubStatus = $"Création de la PR ({head} → {baseBranch})…";
            GitHubPullRequest created = await _s.GitHub.CreatePullRequestAsync(
                new CreatePullRequestOptions(
                    owner,
                    repo,
                    title,
                    head,
                    baseBranch,
                    "Créée depuis Keyce Code."));

            NewPrTitle = "";
            GitHubStatus = $"✓ PR #{created.Number} créée : {created.HtmlUrl}";
            StatusMessage = GitHubStatus;
            Log($"[GitHub] {GitHubStatus}");
            ShowOutput();
            await LoadGitHubPullRequests();
        }
        catch (Exception ex)
        {
            GitHubStatus = FormatGitHubErrorMessage(ex);
            StatusMessage = GitHubStatus;
            Log("[GitHub] " + ex.Message);
            ShowOutput();
        }
    }

    private static string FormatGitHubErrorMessage(Exception ex)
    {
        if (ex.Message.Contains("Hôte inconnu", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub en ligne inaccessible (hors-ligne ou DNS). Pour ouvrir un autre projet sur ce PC, cliquez sur « Ouvrir un autre projet... » ou « Mes projets sur ce PC ».";
        }

        return "Erreur GitHub : " + ex.Message;
    }

    private void ReportErrorMessage(Exception ex)
    {
        StatusMessage = ex.Message;
        Log("Erreur : " + ex.Message);
    }

    // =====================================================================
    //  Exécution simple (IExecutionService), indépendante du débogueur
    // =====================================================================

    private ExecutionSessionId? _executionSessionId;
    private readonly List<ExecutionOutputEventArgs> _pendingExecutionOutput = new();

    [ObservableProperty] private string _executionExecutable = "";
    [ObservableProperty] private string _executionArguments = "";
    [ObservableProperty] private string _executionWorkingDirectory = "";

    // 0 = UTF-8 (65001), 1 = CP850.
    [ObservableProperty] private int _executionOutputEncodingIndex;

    public int ExecutionOutputCodePage => ExecutionOutputEncodingIndex == 1 ? 850 : 65001;

    partial void OnExecutionOutputEncodingIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ExecutionOutputCodePage));
    }

    [ObservableProperty] private string _executionStatus = "Aucun programme en cours d'exécution.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExecutionIdle))]
    private bool _isExecutionStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExecutionIdle))]
    private bool _isExecuting;

    public bool IsExecutionIdle => !IsExecuting && !IsExecutionStarting;

    [RelayCommand]
    private async Task StartExecution()
    {
        if (!IsExecutionIdle)
            return;

        string executable = ExecutionExecutable.Trim();
        if (executable.Length == 0)
        {
            ExecutionStatus = "Indiquez le programme à exécuter (par exemple dotnet, python ou un fichier .exe).";
            return;
        }

        IReadOnlyList<string> arguments;
        try
        {
            arguments = ParseExecutionArguments(ExecutionArguments);
        }
        catch (FormatException ex)
        {
            ExecutionStatus = ex.Message;
            return;
        }

        string workingDirectory = ExecutionWorkingDirectory.Trim();
        string? workingDirectoryOption = workingDirectory.Length == 0 ? null : workingDirectory;

        _pendingExecutionOutput.Clear();
        IsExecutionStarting = true;
        ExecutionStatus = "Démarrage du programme…";

        try
        {
            var options = new ExecutionStartOptions(
                executable,
                arguments,
                workingDirectoryOption,
                OutputCodePage: ExecutionOutputCodePage);

            ExecutionSessionId id = await _s.Execution.StartExecutionAsync(options);
            _executionSessionId = id;
            IsExecuting = true;
            IsExecutionStarting = false;

            foreach (ExecutionOutputEventArgs pending in _pendingExecutionOutput)
            {
                if (pending.SessionId == id)
                    AppendExecutionOutput(pending);
            }

            _pendingExecutionOutput.Clear();
            ExecutionStatus = "Programme en cours d'exécution.";
            Log($"Exécution démarrée : {executable}");
            ShowOutput();
            _ = MonitorExecutionAsync(id);
        }
        catch (Exception ex)
        {
            _executionSessionId = null;
            _pendingExecutionOutput.Clear();
            IsExecutionStarting = false;
            IsExecuting = false;
            ExecutionStatus = "Échec du démarrage : " + ex.Message;
            Log("Exécution : " + ex.Message);
            ShowOutput();
        }
    }

    [ObservableProperty] private string _executionInput = "";

    /// <summary>Envoie la ligne saisie au programme en cours (entrée standard) et l'affiche dans la sortie.</summary>
    [RelayCommand]
    private async Task SendExecutionInput()
    {
        if (_executionSessionId is not { } id)
            return;

        string text = ExecutionInput;
        ExecutionInput = "";

        // Il n'y a pas de terminal pour afficher la saisie : on la reproduit dans la sortie.
        OutputText += text + "\n";

        try
        {
            await _s.Execution.SendInputAsync(id, text + Environment.NewLine);
        }
        catch (Exception ex)
        {
            ExecutionStatus = "Saisie impossible : " + ex.Message;
            Log("Saisie : " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopExecution()
    {
        if (_executionSessionId is not { } id)
            return;

        ExecutionStatus = "Arrêt du programme…";
        try
        {
            await _s.Execution.StopExecutionAsync(id);
        }
        catch (Exception ex)
        {
            ExecutionStatus = "Erreur lors de l'arrêt : " + ex.Message;
            Log("Arrêt du processus : " + ex.Message);
        }
    }

    private async Task MonitorExecutionAsync(ExecutionSessionId id)
    {
        try
        {
            ExecutionResult result = await _s.Execution.WaitForExitAsync(id);
            UI(() =>
            {
                if (_executionSessionId != id)
                    return;

                _executionSessionId = null;
                IsExecuting = false;
                ExecutionStatus = $"Programme terminé (code de sortie : {result.ExitCode}).";
                Log($"Processus terminé avec le code {result.ExitCode}.");
                ShowOutput();
            });
        }
        catch (Exception ex)
        {
            UI(() =>
            {
                if (_executionSessionId != id)
                    return;

                _executionSessionId = null;
                IsExecuting = false;
                ExecutionStatus = "Erreur pendant l'exécution : " + ex.Message;
                Log("Exécution : " + ex.Message);
                ShowOutput();
            });
        }
    }

    private void OnExecutionOutputReceived(ExecutionOutputEventArgs e)
    {
        if (_executionSessionId is { } activeId)
        {
            if (activeId == e.SessionId)
                AppendExecutionOutput(e);

            return;
        }

        // Le processus peut produire ses premiers caractères avant que StartExecutionAsync
        // ait rendu son identifiant à l'interface. On les garde brièvement en attente.
        if (IsExecutionStarting)
            _pendingExecutionOutput.Add(e);
    }

    private void AppendExecutionOutput(ExecutionOutputEventArgs e)
    {
        if (e.Stream == ExecutionOutputStream.StandardError)
            OutputText += "[stderr] " + e.Data;
        else
            OutputText += e.Data;

        ShowOutput();
    }

    private static IReadOnlyList<string> ParseExecutionArguments(string input)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool insideQuotes = false;
        bool tokenStarted = false;

        for (int i = 0; i < input.Length; i++)
        {
            char character = input[i];

            if (character == '\\' && insideQuotes && i + 1 < input.Length
                && input[i + 1] is '"' or '\\')
            {
                current.Append(input[++i]);
                tokenStarted = true;
                continue;
            }

            if (character == '"')
            {
                insideQuotes = !insideQuotes;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !insideQuotes)
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (insideQuotes)
            throw new FormatException("Les guillemets des arguments ne sont pas équilibrés.");

        if (tokenStarted)
            arguments.Add(current.ToString());

        return arguments;
    }

    // =====================================================================
    //  Bouton ▶ : exécute le fichier actif (le langage est détecté automatiquement)
    //  et « Déboguer » : compile le projet .NET du fichier actif puis lance le débogueur.
    // =====================================================================

    [RelayCommand]
    private async Task RunActiveFile()
    {
        if (!IsExecutionIdle)
            return;

        RunPlan? plan = await PreparePlanAsync(path => _s.RunPlanner.PlanRun(path));
        if (plan is null)
            return;

        await ExecutePlanAsync(plan);
    }

    [RelayCommand]
    private async Task DebugActiveFile()
    {
        if (IsDebugging || !IsExecutionIdle)
            return;

        string? sourcePath = ActiveTab?.Path;

        RunPlan? plan = await PreparePlanAsync(path => _s.RunPlanner.PlanDebugBuild(path));
        if (plan is null || sourcePath is null)
        {
            DebugStatus = ExecutionStatus;
            return;
        }

        DebugStatus = "Compilation du projet…";
        int? exitCode = await ExecutePlanAsync(plan);
        if (exitCode != 0)
        {
            DebugStatus = "La compilation a échoué : débogage annulé.";
            return;
        }

        try
        {
            DebugProgram = _s.RunPlanner.FindDebugAssembly(sourcePath);
        }
        catch (Exception ex)
        {
            DebugStatus = ex.Message;
            Log("Débogage : " + ex.Message);
            return;
        }

        await StartDebug();
    }

    /// <summary>Vérifie le fichier actif (enregistré, sauvegardé) puis demande son plan au planificateur.</summary>
    private async Task<RunPlan?> PreparePlanAsync(Func<string, RunPlan> planner)
    {
        DocumentTab? tab = ActiveTab;
        if (tab is null)
        {
            ReportRunProblem("Ouvrez un fichier pour l'exécuter.");
            return null;
        }

        string? path = tab.Path;
        if (path is null)
        {
            ReportRunProblem("Enregistrez le fichier avant de l'exécuter.");
            return null;
        }

        if (tab.IsDirty && !await SaveAsync(tab))
            return null;

        try
        {
            return planner(path);
        }
        catch (Exception ex)
        {
            ReportRunProblem(ex.Message);
            return null;
        }
    }

    private void ReportRunProblem(string message)
    {
        ExecutionStatus = message;
        StatusMessage = message;
        Log("Exécution : " + message);
        ShowOutput();
    }

    /// <summary>Exécute les étapes du plan dans l'ordre. Retourne le code de sortie de la dernière étape lancée (null si le démarrage a échoué).</summary>
    private async Task<int?> ExecutePlanAsync(RunPlan plan)
    {
        Log($"▶ {plan.DisplayName}");

        int? exitCode = null;
        int lastIndex = plan.Steps.Count - 1;
        int stepIndex = 0;

        try
        {
            for (; stepIndex <= lastIndex; stepIndex++)
            {
                RunStep step = plan.Steps[stepIndex];
                Log(FormatCommand(step));

                exitCode = await RunStepAsync(step);
                if (exitCode != 0)
                    break;
            }
        }
        finally
        {
            _executionSessionId = null;
            IsExecuting = false;
            IsExecutionStarting = false;
        }

        if (exitCode is { } code)
        {
            bool compileFailed = code != 0 && stepIndex < lastIndex;
            ExecutionStatus = compileFailed
                ? $"La compilation a échoué (code de sortie : {code})."
                : $"Programme terminé (code de sortie : {code}).";
            Log(ExecutionStatus);
        }

        ShowOutput();
        return exitCode;
    }

    /// <summary>Lance une étape et attend sa fin. Retourne son code de sortie, ou null si elle n'a pas pu démarrer.</summary>
    private async Task<int?> RunStepAsync(RunStep step)
    {
        _pendingExecutionOutput.Clear();
        IsExecutionStarting = true;
        ExecutionStatus = step.Description + "…";

        ExecutionSessionId id;
        try
        {
            ExecutionStartOptions options = step.Options with { OutputCodePage = ExecutionOutputCodePage };
            id = await _s.Execution.StartExecutionAsync(options);
        }
        catch (Exception ex)
        {
            _executionSessionId = null;
            _pendingExecutionOutput.Clear();
            IsExecutionStarting = false;
            IsExecuting = false;
            ExecutionStatus = "Échec du démarrage : " + ex.Message;
            Log("Exécution : " + ex.Message);
            ShowOutput();
            return null;
        }

        _executionSessionId = id;
        IsExecuting = true;
        IsExecutionStarting = false;

        foreach (ExecutionOutputEventArgs pending in _pendingExecutionOutput)
        {
            if (pending.SessionId == id)
                AppendExecutionOutput(pending);
        }

        _pendingExecutionOutput.Clear();
        ExecutionStatus = step.Description + " (en cours)…";
        ShowOutput();

        try
        {
            ExecutionResult result = await _s.Execution.WaitForExitAsync(id);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            ExecutionStatus = "Erreur pendant l'exécution : " + ex.Message;
            Log("Exécution : " + ex.Message);
            return null;
        }
    }

    private static string FormatCommand(RunStep step)
    {
        IEnumerable<string> arguments = step.Options.Arguments ?? Array.Empty<string>();
        string joined = string.Join(' ', arguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

        return ("> " + step.Options.Executable + " " + joined).TrimEnd();
    }

    // =====================================================================
    //  Exécuter et déboguer (IDebugService, via NetCoreDbg)
    // =====================================================================

    private readonly List<Breakpoint> _breakpoints = new();
    private DebugSessionId? _debugId;

    public ObservableCollection<string> Breakpoints { get; } = new();

    [ObservableProperty] private string _debugProgram = "";
    [ObservableProperty] private string _debugStatus = "Aucune session de débogage.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugIdle))]
    private bool _isDebugging;

    public bool IsDebugIdle => !IsDebugging;

    public void AddBreakpointAtCaret()
    {
        if (ActiveTab?.Path is not { } path)
        {
            DebugStatus = "Ouvrez un fichier enregistré pour poser un point d'arrêt.";
            return;
        }

        int line;
        try { line = _s.Editor.GetCaretSet(ActiveTab.Id).Primary.Position.Line + 1; }
        catch (AppException ex) { ReportError(ex); return; }

        var bp = new Breakpoint(path, line);
        if (_breakpoints.Contains(bp)) return;

        _breakpoints.Add(bp);
        Breakpoints.Add($"{Path.GetFileName(path)} : ligne {line}");
        DebugStatus = $"Point d'arrêt ajouté ({Path.GetFileName(path)}, ligne {line}).";
        if (_debugId is { } id) _ = ApplyBreakpointAsync(id, bp);
    }

    private async Task ApplyBreakpointAsync(DebugSessionId id, Breakpoint bp)
    {
        try { await _s.Debugger.SetBreakpointAsync(id, bp); }
        catch (Exception ex) { Log("Point d'arrêt : " + ex.Message); }
    }

    [RelayCommand]
    private void ClearBreakpoints()
    {
        _breakpoints.Clear();
        Breakpoints.Clear();
        DebugStatus = "Liste vidée (effective à la prochaine session).";
    }

    [RelayCommand]
    private async Task StartDebug()
    {
        if (IsDebugging) return;
        string program = DebugProgram.Trim();
        if (program.Length == 0)
        {
            DebugStatus = "Indiquez le fichier .dll à déboguer.";
            return;
        }

        try
        {
            DebugStatus = "Démarrage…";
            var id = await _s.Debugger.StartDebugAsync(new DebugStartOptions(program));
            _debugId = id;
            IsDebugging = true;

            foreach (var bp in _breakpoints.ToList())
                await _s.Debugger.SetBreakpointAsync(id, bp);

            DebugStatus = "En pause au point d'entrée. Continuez ou avancez pas à pas.";
            Log("Débogage démarré : " + program);
        }
        catch (Exception ex)
        {
            _debugId = null;
            IsDebugging = false;
            DebugStatus = ex.Message;
            Log("Débogage : " + ex.Message);
        }
    }

    [RelayCommand] private Task DebugContinue() => DebugStepAsync(StepKind.Continue);
    [RelayCommand] private Task DebugOver() => DebugStepAsync(StepKind.Over);
    [RelayCommand] private Task DebugInto() => DebugStepAsync(StepKind.Into);
    [RelayCommand] private Task DebugOut() => DebugStepAsync(StepKind.Out);

    private async Task DebugStepAsync(StepKind kind)
    {
        if (_debugId is not { } id) return;
        try
        {
            DebugStatus = kind == StepKind.Continue ? "Exécution…" : "Pas à pas…";
            await _s.Debugger.StepAsync(id, kind);
            DebugStopInfo stop = await _s.Debugger.WaitForStopAsync(id);

            DebugStatus = $"Arrêt : {stop.Reason}" + (stop.Line is { } l ? $", ligne {l}" : "");
            if (stop.SourcePath is { } source && File.Exists(source))
            {
                await OpenPathAsync(source);
                if (stop.Line is { } line) GoToLine(line);
            }
        }
        catch (TimeoutException)
        {
            DebugStatus = "Le programme tourne toujours (aucun arrêt). Posez un point d'arrêt ou arrêtez la session.";
        }
        catch (InvalidOperationException ex)
        {
            // Session terminée (le programme est arrivé au bout).
            EndDebug(ex.Message);
        }
        catch (Exception ex)
        {
            DebugStatus = ex.Message;
            Log("Débogage : " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopDebug()
    {
        if (_debugId is not { } id) return;
        try { await _s.Debugger.StopDebugAsync(id); }
        catch (Exception ex) { Log("Débogage : " + ex.Message); }
        EndDebug("Session arrêtée.");
    }

    private void EndDebug(string message)
    {
        _debugId = null;
        IsDebugging = false;
        DebugStatus = message;
        Log("Débogage : " + message);
    }

    // =====================================================================
    //  Extensions (ExtensionManager)
    // =====================================================================

    public ObservableCollection<ExtensionItem> ExtensionItems { get; } = new();
    public ObservableCollection<CatalogExtensionItem> CatalogItems { get; } = new();
    public ObservableCollection<CommandInfo> ExtensionCommands { get; } = new();

    [ObservableProperty] private string _extensionsInfo = "Aucune extension installée.";
    [ObservableProperty] private string _catalogSearchQuery = "";
    [ObservableProperty] private bool _isCatalogInstalling;
    [ObservableProperty] private double _catalogInstallProgress;
    [ObservableProperty] private string _catalogProgressText = "";
    [ObservableProperty] private string _aiPromptText = "";
    [ObservableProperty] private string _aiResponseText = "Posez une question à l'IA, générez du code ou modifiez le fichier ouvert.";

    private CancellationTokenSource? _catalogSearchCts;
    private CancellationTokenSource? _installProofCts;
    private string? _recentlyInstalledExtensionId;
    private bool _isCatalogBusy;

    partial void OnCatalogSearchQueryChanged(string value) =>
        _ = DebouncedSearchCatalogAsync();

    [RelayCommand]
    private Task SearchCatalog() => RefreshCatalogAsync();

    private async Task DebouncedSearchCatalogAsync()
    {
        _catalogSearchCts?.Cancel();
        _catalogSearchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _catalogSearchCts = cts;

        try
        {
            await Task.Delay(300, cts.Token);
            await RefreshCatalogAsync(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    private async Task RefreshExtensionsAsync(bool updateSummary = false)
    {
        try
        {
            IReadOnlyList<ExtensionUpdateInfo> updates = [];
            try { updates = await _s.ExtensionCatalog.CheckUpdatesAsync(); }
            catch (Exception) { }

            var updatesById = updates.ToDictionary(
                u => u.ExtensionId,
                u => u.AvailableVersion,
                StringComparer.OrdinalIgnoreCase);

            ExtensionItems.Clear();
            foreach (ExtensionInfo info in _s.Extensions.Extensions.OrderBy(e => e.Manifest.Name))
            {
                updatesById.TryGetValue(info.Manifest.Id, out string? nextVersion);
                bool showProof = string.Equals(
                    info.Manifest.Id,
                    _recentlyInstalledExtensionId,
                    StringComparison.OrdinalIgnoreCase);
                ExtensionItems.Add(new ExtensionItem(info, nextVersion, showProof));
            }

            if (updateSummary)
            {
                ExtensionsInfo = ExtensionItems.Count == 0
                    ? "Aucune extension installée."
                    : $"{ExtensionItems.Count} extension(s) installée(s)";
            }

            await RefreshCatalogAsync(updatesById);
            RefreshExtensionCommands();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    private async Task RefreshCatalogAsync(
        Dictionary<string, string>? updatesById = null,
        CancellationToken ct = default)
    {
        try
        {
            if (updatesById is null)
            {
                IReadOnlyList<ExtensionUpdateInfo> updates = [];
                try { updates = await _s.ExtensionCatalog.CheckUpdatesAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }

                updatesById = updates.ToDictionary(
                    u => u.ExtensionId,
                    u => u.AvailableVersion,
                    StringComparer.OrdinalIgnoreCase);
            }

            IReadOnlyList<ExtensionCatalogEntry> entries = [];
            try { entries = await _s.ExtensionCatalog.SearchAsync(CatalogSearchQuery, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            if (ct.IsCancellationRequested)
                return;

            var installedIds = _s.Extensions.Extensions
                .Select(e => e.Manifest.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            CatalogItems.Clear();
            foreach (ExtensionCatalogEntry entry in entries)
            {
                CatalogItems.Add(new CatalogExtensionItem(
                    entry,
                    installedIds.Contains(entry.Manifest.Id),
                    updatesById.ContainsKey(entry.Manifest.Id)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    private void RefreshExtensionCommands()
    {
        ExtensionCommands.Clear();
        foreach (CommandInfo cmd in _s.CommandRegistry.Commands)
            ExtensionCommands.Add(cmd);
    }

    public async Task InstallExtensionAsync(string folder)
    {
        try
        {
            var info = await _s.Extensions.InstallAsync(folder);
            ExtensionsInfo = $"Extension installée : {info.Manifest.Name}";
            StatusMessage = ExtensionsInfo;
            Log(ExtensionsInfo);
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    public async Task UpdateExtensionAsync(string folder)
    {
        try
        {
            var info = await _s.Extensions.UpdateAsync(folder);
            ExtensionsInfo = $"Extension mise à jour : {info.Manifest.Name} v{info.Manifest.Version}";
            StatusMessage = ExtensionsInfo;
            Log(ExtensionsInfo);
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    [RelayCommand]
    private async Task InstallFromCatalog(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!TryBeginCatalogTransfer($"Préparation du téléchargement de '{id}'…"))
            return;

        try
        {
            _recentlyInstalledExtensionId = id;
            ExtensionInfo info = await _s.ExtensionCatalog.InstallFromCatalogAsync(id);
            _recentlyInstalledExtensionId = info.Manifest.Id;
            CatalogProgressText = $"Activation de '{info.Manifest.Name}'…";
            CatalogInstallProgress = 98;
            await _s.Extensions.ActivateAsync(info.Manifest.Id);

            CatalogInstallProgress = 100;
            string[] extracted = Directory.Exists(info.InstallPath)
                ? Directory.GetFiles(info.InstallPath, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(info.InstallPath, f))
                    .ToArray()
                : [];
            int cmdCount = _s.CommandRegistry.Commands
                .Count(c => string.Equals(c.OwnerId, info.Manifest.Id, StringComparison.OrdinalIgnoreCase));

            ExtensionsInfo =
                $"✓ '{info.Manifest.Name}' v{info.Manifest.Version} installée ({extracted.Length} fichier(s) extraits, {cmdCount} commande(s)).";
            CatalogProgressText = ExtensionsInfo;
            StatusMessage = ExtensionsInfo;
            Log($"{ExtensionsInfo}\nDossier : {info.InstallPath}\nFichiers : {string.Join(", ", extracted.Take(20))}");
            ShowOutput();
            await RefreshExtensionsAsync();
            _ = ClearInstallProofAfterDelayAsync(info.Manifest.Id);
        }
        catch (Exception ex)
        {
            _recentlyInstalledExtensionId = null;
            ReportExtensionError(ex);
        }
        finally
        {
            IsCatalogInstalling = false;
            _isCatalogBusy = false;
        }
    }

    private async Task ClearInstallProofAfterDelayAsync(string extensionId)
    {
        _installProofCts?.Cancel();
        _installProofCts?.Dispose();
        var cts = new CancellationTokenSource();
        _installProofCts = cts;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            if (string.Equals(_recentlyInstalledExtensionId, extensionId, StringComparison.OrdinalIgnoreCase))
            {
                _recentlyInstalledExtensionId = null;
                UI(() => _ = RefreshExtensionsAsync(updateSummary: true));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task UpdateFromCatalog(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!TryBeginCatalogTransfer($"Mise à jour de '{id}' en cours…"))
            return;

        try
        {
            ExtensionInfo info = await _s.ExtensionCatalog.UpdateFromCatalogAsync(id);
            CatalogInstallProgress = 100;
            ExtensionsInfo = $"✓ Extension '{info.Manifest.Name}' mise à jour en v{info.Manifest.Version} !";
            CatalogProgressText = ExtensionsInfo;
            StatusMessage = ExtensionsInfo;
            Log(ExtensionsInfo);
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
        finally
        {
            IsCatalogInstalling = false;
            _isCatalogBusy = false;
        }
    }

    private bool TryBeginCatalogTransfer(string initialText)
    {
        if (_isCatalogBusy)
        {
            ExtensionsInfo = "Téléchargement déjà en cours, veuillez patienter…";
            StatusMessage = ExtensionsInfo;
            return false;
        }

        _isCatalogBusy = true;
        IsCatalogInstalling = true;
        CatalogInstallProgress = 2;
        CatalogProgressText = initialText;
        ExtensionsInfo = initialText;
        StatusMessage = initialText;
        return true;
    }

    [RelayCommand]
    private async Task OpenExtensionReadme(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        ExtensionInfo? info = _s.Extensions.Extensions
            .FirstOrDefault(e => string.Equals(e.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        if (info is null || !Directory.Exists(info.InstallPath))
            return;

        string[] candidates = ["README.md", "package.json", "CHANGELOG.md", "extension.json"];
        foreach (string fileName in candidates)
        {
            string candidatePath = Path.Combine(info.InstallPath, fileName);
            if (File.Exists(candidatePath))
            {
                await OpenPathAsync(candidatePath);
                return;
            }
        }
    }

    [RelayCommand]
    private async Task ExecuteExtensionCommand(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        try
        {
            CommandInfo? cmd = _s.CommandRegistry.Commands
                .FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.Ordinal));

            await _s.CommandRegistry.ExecuteAsync(commandId);

            string? output = null;
            if (cmd?.OwnerId is { } ownerId)
            {
                ExtensionInfo? owner = _s.Extensions.Extensions
                    .FirstOrDefault(e => string.Equals(
                        e.Manifest.Id, ownerId, StringComparison.OrdinalIgnoreCase));
                if (owner is not null)
                {
                    string outputPath = Path.Combine(owner.InstallPath, "last-output.txt");
                    if (File.Exists(outputPath))
                        output = await File.ReadAllTextAsync(outputPath);
                }
            }

            string summary = string.IsNullOrWhiteSpace(output)
                ? $"Commande '{commandId}' exécutée avec succès."
                : output.Trim();

            ExtensionsInfo = summary;
            StatusMessage = summary;
            Log($"[{commandId}] {summary}");
            ShowOutput();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
            ShowOutput();
        }
    }

    [RelayCommand]
    private async Task SendAiPrompt()
    {
        string prompt = AiPromptText.Trim();
        if (prompt.Length == 0)
        {
            AiResponseText = "Écrivez une question pour l'IA.";
            return;
        }

        try
        {
            StatusMessage = "L'IA réfléchit…";
            AiResponseText = "Réflexion en cours…";

            AiCodeContext? codeContext = GetActiveCodeContext();

            var request = new AiChatRequest(
                [
                    new AiChatMessage(
                        AiChatRole.System,
                        "Tu es l'assistant IA intégré à l'éditeur Keyce Code. Réponds clairement en français à toute question générale ou de programmation."),
                    new AiChatMessage(AiChatRole.User, prompt)
                ],
                codeContext);

            AiChatResponse response = await _s.AiAssistant.ChatAsync(request);
            AiResponseText = response.Content;
            AiPromptText = "";
            Log($"[IA - {response.Model}]\nQ: {prompt}\nR: {response.Content}");
            ShowOutput();
            StatusMessage = $"Réponse IA reçue ({response.Model}).";
        }
        catch (Exception ex)
        {
            ReportAiError(ex);
        }
    }

    [RelayCommand]
    private async Task AiWriteOrModifyCode()
    {
        string instruction = AiPromptText.Trim();
        bool hasCurrentCode = ActiveTab is not null && !string.IsNullOrWhiteSpace(FileContent);

        if (instruction.Length == 0)
        {
            if (hasCurrentCode)
            {
                instruction = "Corrige les bugs éventuels, améliore et complète ce code.";
            }
            else
            {
                AiResponseText = "Décrivez le code que l'IA doit écrire ou modifier dans la zone de texte.";
                StatusMessage = AiResponseText;
                return;
            }
        }

        string? currentCode = hasCurrentCode ? TrimCodeForAi(FileContent) : null;
        string? languageId = ActiveTab is not null
            ? _s.Editor.GetInfo(ActiveTab.Id).LanguageId
            : null;

        try
        {
            StatusMessage = "L'IA génère le code…";
            AiResponseText = "Génération / modification du code en cours…";

            string generatedCode = await _s.AiAssistant.GenerateOrModifyCodeAsync(
                instruction,
                currentCode,
                languageId);

            if (string.IsNullOrWhiteSpace(generatedCode))
            {
                AiResponseText = "L'IA n'a retourné aucun code exploitable.";
                StatusMessage = AiResponseText;
                return;
            }

            ApplyGeneratedCodeToActiveEditor(generatedCode);
            AiPromptText = "";
            AiResponseText = "Code généré / modifié directement dans l'éditeur (Ctrl+Z pour annuler).";
            StatusMessage = AiResponseText;
            Log($"[IA Code]\nConsigne : {instruction}\n{generatedCode}");
            ShowOutput();
        }
        catch (Exception ex)
        {
            ReportAiError(ex);
        }
    }

    [RelayCommand]
    private void ApplyAiResponseToEditor()
    {
        string raw = AiResponseText;
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("Posez ", StringComparison.Ordinal)
            || raw.StartsWith("Erreur ", StringComparison.Ordinal)
            || raw.StartsWith("Code généré ", StringComparison.Ordinal)
            || raw.StartsWith("Réflexion en cours", StringComparison.Ordinal)
            || raw.StartsWith("Génération ", StringComparison.Ordinal)
            || raw.StartsWith("Analyse ", StringComparison.Ordinal)
            || raw.StartsWith("Aucune réponse", StringComparison.Ordinal)
            || raw.StartsWith("Écrivez ", StringComparison.Ordinal)
            || raw.StartsWith("Décrivez ", StringComparison.Ordinal)
            || raw.StartsWith("Ouvrez ", StringComparison.Ordinal))
        {
            StatusMessage = "Aucune réponse de code IA à insérer pour le moment.";
            return;
        }

        string code = _s.ExtractCodeBlock(raw);
        if (string.IsNullOrWhiteSpace(code))
        {
            StatusMessage = "Aucun bloc de code détecté dans la réponse IA.";
            return;
        }

        ApplyGeneratedCodeToActiveEditor(code);
        StatusMessage = "Code IA inséré dans l'éditeur (Ctrl+Z pour annuler).";
        Log(StatusMessage);
    }

    [RelayCommand]
    private Task ExplainActiveCodeWithAi() =>
        RunAiActionAsync("Explication IA", (code, lang) => _s.AiAssistant.ExplainCodeAsync(code, lang));

    [RelayCommand]
    private Task SuggestImprovementsWithAi() =>
        RunAiActionAsync(
            "Suggestions IA",
            (code, lang) => _s.AiAssistant.SuggestFixAsync(
                code,
                "Vérifie les bugs potentiels et propose des améliorations",
                lang));

    private AiCodeContext? GetActiveCodeContext()
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(FileContent))
            return null;

        string? languageId = _s.Editor.GetInfo(ActiveTab.Id).LanguageId;
        return new AiCodeContext(
            ActiveTab.Path ?? ActiveTab.Title,
            languageId,
            TrimCodeForAi(FileContent));
    }

    private void ApplyGeneratedCodeToActiveEditor(string code)
    {
        if (ActiveTab is null)
            NewFile();

        ExecuteEdit((c, id) =>
        {
            c.SelectAll(id);
            c.Paste(id, code);
        });
    }

    private async Task RunAiActionAsync(
        string label,
        Func<string, string?, Task<string>> aiCall)
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(FileContent))
        {
            AiResponseText = "Ouvrez un fichier non vide pour analyser votre code avec l'IA.";
            StatusMessage = AiResponseText;
            return;
        }

        try
        {
            StatusMessage = "Analyse IA en cours…";
            AiResponseText = "Analyse du fichier en cours…";
            string? languageId = _s.Editor.GetInfo(ActiveTab.Id).LanguageId;
            string result = await aiCall(TrimCodeForAi(FileContent), languageId);
            AiResponseText = result;
            Log($"{label} ({ActiveTab.Title}) :\n{result}");
            ShowOutput();
            ExtensionsInfo = $"{label} affichée.";
            StatusMessage = ExtensionsInfo;
        }
        catch (Exception ex)
        {
            ReportAiError(ex);
            ExtensionsInfo = AiResponseText;
        }
    }

    private void ReportExtensionError(Exception ex)
    {
        ExtensionsInfo = "Erreur : " + ex.Message;
        StatusMessage = ex.Message;
        Log("Erreur : " + ex.Message);
    }

    private void ReportAiError(Exception ex)
    {
        AiResponseText = "Erreur IA : " + ex.Message;
        StatusMessage = ex.Message;
        Log("Erreur : " + ex.Message);
        ShowOutput();
    }

    private static string TrimCodeForAi(string code) =>
        code.Length <= 6000 ? code : code[..6000];

    [RelayCommand]
    private async Task ToggleExtension(string? id)
    {
        var info = _s.Extensions.Extensions.FirstOrDefault(e => e.Manifest.Id == id);
        if (info is null) return;
        try
        {
            if (info.State == ExtensionState.Active)
            {
                await _s.Extensions.DeactivateAsync(info.Manifest.Id);
                ExtensionsInfo = $"Extension désactivée : {info.Manifest.Name}";
            }
            else
            {
                await _s.Extensions.ActivateAsync(info.Manifest.Id);
                ExtensionsInfo = $"Extension activée : {info.Manifest.Name}";
            }

            StatusMessage = ExtensionsInfo;
            Log(ExtensionsInfo);
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }

    [RelayCommand]
    private async Task UninstallExtension(string? id)
    {
        if (id is null) return;
        try
        {
            await _s.Extensions.UninstallAsync(id);
            ExtensionsInfo = $"Extension désinstallée : {id}";
            StatusMessage = ExtensionsInfo;
            Log(ExtensionsInfo);
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            ReportExtensionError(ex);
        }
    }
}

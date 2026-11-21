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

    /// <summary>À appeler une fois au démarrage (charge les extensions installées).</summary>
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
    }

    // =====================================================================
    //  Git : état du dépôt
    // =====================================================================

    [ObservableProperty] private bool _isGitRepoMissing;
    [ObservableProperty] private bool _hasGitError;
    [ObservableProperty] private string _gitMessage = "";

    [ObservableProperty] private int _stagedCount;
    [ObservableProperty] private int _unstagedCount;

    private void UpdateGitCounts()
    {
        StagedCount = Git.Controller.StagedFiles.Count;
        UnstagedCount = Git.Controller.UnstagedFiles.Count;
    }

    private void UpdateGitState()
    {
        string? msg = Git.Controller.ErrorMessage;
        bool missing = msg?.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) == true;
        IsGitRepoMissing = missing;
        HasGitError = !missing && !string.IsNullOrEmpty(msg);
        GitMessage = missing ? "Ce dossier n'est pas un dépôt Git." : msg ?? "";
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
        catch (AppException ex) { ReportError(ex); }
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
                ExtensionItems.Add(new ExtensionItem(info, nextVersion));
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
            ExtensionInfo info = await _s.ExtensionCatalog.InstallFromCatalogAsync(id);
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
            await OpenExtensionReadme(info.Manifest.Id);
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        };
        _s.Extensions.ExtensionChanged += (_, _) => UI(RefreshExtensions);
        _s.CommandRegistry.CommandsChanged += (_, _) => UI(RefreshExtensionCommands);
    }

    /// <summary>À appeler une fois au démarrage (charge les extensions installées).</summary>
    public async Task InitializeAsync()
    {
        try { await _s.Extensions.LoadInstalledAsync(); }
        catch (Exception ex) { Log("Extensions : " + ex.Message); }
        RefreshExtensions();
        RefreshExtensionCommands();
    }

    // =====================================================================
    //  Git : état du dépôt
    // =====================================================================

    [ObservableProperty] private bool _isGitRepoMissing;
    [ObservableProperty] private bool _hasGitError;
    [ObservableProperty] private string _gitMessage = "";

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
    public ObservableCollection<CommandInfo> ExtensionCommands { get; } = new();

    [ObservableProperty] private string _extensionsInfo = "Aucune extension installée.";

    private void RefreshExtensions()
    {
        ExtensionItems.Clear();
        foreach (var info in _s.Extensions.Extensions.OrderBy(e => e.Manifest.Name))
            ExtensionItems.Add(new ExtensionItem(info));

        ExtensionsInfo = ExtensionItems.Count == 0
            ? "Aucune extension installée."
            : $"{ExtensionItems.Count} extension(s)";
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
            StatusMessage = $"Extension installée : {info.Manifest.Name}";
            Log(StatusMessage);
        }
        catch (AppException ex) { ReportError(ex); }
    }

    public async Task UpdateExtensionAsync(string folder)
    {
        try
        {
            var info = await _s.Extensions.UpdateAsync(folder);
            StatusMessage = $"Extension mise à jour : {info.Manifest.Name} v{info.Manifest.Version}";
            Log(StatusMessage);
        }
        catch (AppException ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task ExecuteExtensionCommand(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;
        try
        {
            await _s.CommandRegistry.ExecuteAsync(commandId);
            StatusMessage = $"Commande exécutée : {commandId}";
            Log(StatusMessage);
        }
        catch (AppException ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task ExplainActiveCodeWithAi()
    {
        if (ActiveTab is null || string.IsNullOrWhiteSpace(FileContent))
        {
            StatusMessage = "Ouvrez un fichier non vide pour l'expliquer avec l'IA.";
            return;
        }

        try
        {
            StatusMessage = "Analyse IA en cours…";
            string? languageId = _s.Editor.GetInfo(ActiveTab.Id).LanguageId;
            string explanation = await _s.AiAssistant.ExplainCodeAsync(FileContent, languageId);
            Log($"Explication IA ({ActiveTab.Title}) :\n{explanation}");
            ShowOutput();
            StatusMessage = "Explication IA affichée dans Sortie.";
        }
        catch (AppException ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task ToggleExtension(string? id)
    {
        var info = _s.Extensions.Extensions.FirstOrDefault(e => e.Manifest.Id == id);
        if (info is null) return;
        try
        {
            if (info.State == ExtensionState.Active) await _s.Extensions.DeactivateAsync(info.Manifest.Id);
            else await _s.Extensions.ActivateAsync(info.Manifest.Id);
        }
        catch (AppException ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task UninstallExtension(string? id)
    {
        if (id is null) return;
        try { await _s.Extensions.UninstallAsync(id); }
        catch (AppException ex) { ReportError(ex); }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using App.Core.Settings;

namespace App.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableBase
{
    public const string CommonCategory = "Fréquemment utilisés";
    private readonly List<SettingItemViewModel> _all;
    private string _selected = CommonCategory;
    private string _search = "";
    private string _resultText = "";

    public ObservableCollection<SettingItemViewModel> Items { get; } = new();
    public IReadOnlyList<string> Categories { get; }
    public string SettingsPath { get; }

    public SettingsViewModel(ISettingsService s)
    {
        SettingsPath = s.FilePath;
        _all = BuildCatalog(s);
        Categories = new[] { CommonCategory }.Concat(_all.Select(i => i.Category).Distinct()).ToList();
        Refresh();
    }

    public string SelectedCategory
    {
        get => _selected;
        set { if (value != null && Set(ref _selected, value)) Refresh(); }
    }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Refresh(); }
    }

    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

    private void Refresh()
    {
        IEnumerable<SettingItemViewModel> q;
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = _all.Where(i => words.All(w =>
                i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Key.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                i.Category.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        else q = _selected == CommonCategory ? _all.Where(i => i.IsCommon) : _all.Where(i => i.Category == _selected);

        Items.Clear();
        foreach (var i in q) Items.Add(i);
        ResultText = string.IsNullOrWhiteSpace(_search) ? _selected : $"{Items.Count} paramètre(s) trouvé(s)";
    }

    // ===== Catalogue : ajoutez vos paramètres ici =====
    private static List<SettingItemViewModel> BuildCatalog(ISettingsService s) => new()
    {
        // Éditeur
        new TextSettingViewModel(s, "editor.fontFamily", "Éditeur", "Police", "Contrôle la famille de police.", "Consolas, 'Courier New', monospace") { IsCommon = true },
        new NumberSettingViewModel(s, "editor.fontSize", "Éditeur", "Taille de police", "Contrôle la taille de police en pixels.", 14, 6, 100) { IsCommon = true },
        new NumberSettingViewModel(s, "editor.tabSize", "Éditeur", "Taille de tabulation", "Nombre d'espaces équivalant à une tabulation.", 4, 1, 16) { IsCommon = true },
        new BoolSettingViewModel(s, "editor.insertSpaces", "Éditeur", "Insérer des espaces", "Insère des espaces quand on appuie sur Tab.", true),
        new ChoiceSettingViewModel(s, "editor.wordWrap", "Éditeur", "Retour à la ligne", "Contrôle le retour à la ligne automatique.", "off", "off", "on", "wordWrapColumn") { IsCommon = true },
        new ChoiceSettingViewModel(s, "editor.lineNumbers", "Éditeur", "Numéros de ligne", "Contrôle l'affichage des numéros de ligne.", "on", "on", "off", "relative"),
        new BoolSettingViewModel(s, "editor.minimap.enabled", "Éditeur", "Minimap", "Affiche la minimap du code.", true),
        new ChoiceSettingViewModel(s, "editor.renderWhitespace", "Éditeur", "Afficher les espaces", "Contrôle l'affichage des caractères d'espacement.", "selection", "none", "selection", "all"),
        new ChoiceSettingViewModel(s, "editor.cursorStyle", "Éditeur", "Style du curseur", "Contrôle le style du curseur.", "line", "line", "block", "underline"),
        new BoolSettingViewModel(s, "editor.formatOnSave", "Éditeur", "Formater à l'enregistrement", "Formate le fichier à l'enregistrement.", false),

        // Fichiers
        new ChoiceSettingViewModel(s, "files.autoSave", "Fichiers", "Enregistrement automatique", "Contrôle l'enregistrement automatique des fichiers modifiés.", "off", "off", "afterDelay", "onFocusChange") { IsCommon = true },
        new ChoiceSettingViewModel(s, "files.encoding", "Fichiers", "Encodage", "Encodage par défaut des fichiers.", "utf8", "utf8", "utf8bom", "utf16le", "iso88591"),
        new ChoiceSettingViewModel(s, "files.eol", "Fichiers", "Fin de ligne", "Caractère de fin de ligne par défaut.", "auto", "auto", "\\n", "\\r\\n"),
        new BoolSettingViewModel(s, "files.trimTrailingWhitespace", "Fichiers", "Supprimer les espaces en fin de ligne", "Supprime les espaces de fin à l'enregistrement.", false),
        new BoolSettingViewModel(s, "files.insertFinalNewline", "Fichiers", "Insérer une nouvelle ligne finale", "Ajoute une ligne vide à la fin du fichier.", false),

        // Banc de travail
        new ChoiceSettingViewModel(s, "workbench.colorTheme", "Banc de travail", "Thème de couleur", "Thème clair ou sombre de l'application.", "Dark", "Dark", "Light") { IsCommon = true },
        new ChoiceSettingViewModel(s, "workbench.sideBar.location", "Banc de travail", "Position de la barre latérale", "Côté où s'affiche la barre latérale.", "left", "left", "right"),
        new BoolSettingViewModel(s, "workbench.statusBar.visible", "Banc de travail", "Barre d'état visible", "Affiche la barre d'état en bas.", true),
        new ChoiceSettingViewModel(s, "workbench.startupEditor", "Banc de travail", "Éditeur au démarrage", "Ce qui s'affiche au lancement.", "welcomePage", "none", "welcomePage", "newUntitledFile"),

        // Terminal
        new ChoiceSettingViewModel(s, "terminal.integrated.defaultProfile", "Terminal", "Profil par défaut", "Shell lancé par le terminal intégré.", "PowerShell", "PowerShell", "CMD", "Bash"),
        new NumberSettingViewModel(s, "terminal.integrated.fontSize", "Terminal", "Taille de police", "Taille de police du terminal en pixels.", 14, 6, 100),
        new ChoiceSettingViewModel(s, "terminal.integrated.cursorStyle", "Terminal", "Style du curseur", "Style du curseur du terminal.", "block", "block", "line", "underline"),

        // Git
        new BoolSettingViewModel(s, "git.enabled", "Git", "Activer Git", "Active l'intégration Git.", true),
        new BoolSettingViewModel(s, "git.autofetch", "Git", "Récupération automatique", "Exécute périodiquement git fetch.", false),
        new TextSettingViewModel(s, "git.path", "Git", "Chemin de Git", "Chemin de l'exécutable git (vide = détection auto).", ""),
        new BoolSettingViewModel(s, "git.confirmSync", "Git", "Confirmer la synchronisation", "Demande confirmation avant push/pull.", true),

        // Extensions
        new BoolSettingViewModel(s, "extensions.autoUpdate", "Extensions", "Mise à jour automatique", "Met à jour les extensions automatiquement.", true),
        new BoolSettingViewModel(s, "extensions.autoCheckUpdates", "Extensions", "Vérifier les mises à jour", "Vérifie automatiquement les mises à jour.", true),
    };
}

# Keyce Code

Éditeur de code de bureau inspiré de Visual Studio Code, entièrement développé en **C# / .NET** avec **Avalonia UI** (C# + XAML).
Projet original : aucun code source ni ressource propriétaire de VS Code n'est repris.

> Périmètre : on commence par un **MVP**, puis on ajoute les fonctions avancées par étapes.

## Technologies

| Partie | Choix |
|---|---|
| Interfaces | C# + Avalonia UI + XAML (Windows, Linux, macOS) |
| Langage principal | C# + .NET LTS |
| Moteur d'édition | Contrôle personnalisé Avalonia |
| Fonctions de langage | LSP + serveurs de langage existants |
| Git | Git CLI piloté par C# |
| Terminal | Processus + PTY / pseudo-terminal |
| Débogage | Debug Adapter Protocol (DAP) |
| Tests | xUnit + tests d'intégration |

## Débogage multi-langages

Le débogage repose sur le **Debug Adapter Protocol (DAP)** : un adaptateur par langage, déclaré dans `DebugAdapterRegistry` (`src/App.Integrations/DebugAdapters.cs`). F5 compile le fichier actif si nécessaire, puis lance le débogueur.

| Langage | Adaptateur | Installation |
| ------- | ---------- | ------------ |
| C# (.NET) | NetCoreDbg | `scoop install netcoredbg` |
| Python | debugpy | `python -m pip install debugpy` |
| Go | Delve (`dlv dap`) | `go install github.com/go-delve/delve/cmd/dlv@latest` |
| C / C++ / Rust | lldb-dap (LLVM) | installer LLVM / LLDB |

La variable d'environnement `KEYCE_DEBUGGER_<LANGAGE>` (`CSHARP`, `PYTHON`, `GO`, `NATIVE`) permet d'indiquer le chemin exact de l'outil. Pour ajouter un langage, créer une `DebugAdapterDefinition` et l'ajouter au registre.

## Architecture

L'interface ne gère pas directement Git, les processus ou l'analyse des langages : elle appelle des services internes via des contrats stables.

```
Interface Avalonia → API interne / événements → Services C# → Adaptateurs → LSP / DAP / Git / Shell / compilateurs
```

| Couche | Rôle |
|---|---|
| Présentation Avalonia | Fenêtres, menus, panneaux, onglets, éditeur visuel, thèmes, raccourcis, accessibilité |
| Application / orchestration | Commandes, cas d'utilisation, espaces de travail, événements |
| Services du cœur | Documents, édition, fichiers, recherche, processus, paramètres, journalisation |
| Adaptateurs externes | LSP, DAP, Git, shells, compilateurs, outils tiers |

Contrats principaux : `IWorkspaceService`, `IFileService`, `IEditorService`, `ILanguageClient`, `IGitService`, `ITerminalService`, `IDebugService`.

## Structure du dépôt

```
src/
  App.Desktop/        Application Avalonia, vues C# / XAML
  App.Application/    Cas d'utilisation et orchestration
  App.Core/           Modèles et contrats partagés
  App.Editor/         Moteur d'édition et documents
  App.Languages/      Client LSP et fonctionnalités de langage
  App.Extensions/     API et gestionnaire d'extensions
  App.Integrations/   Git, terminal, tâches et débogage
tests/
  App.Core.Tests/
  App.Editor.Tests/
  App.Extensions.Tests/
  App.Integration.Tests/
  App.Languages.Sandbox/   Application console de vérification manuelle (langages)
```

## Prérequis

- SDK .NET (LTS)
- Git

## Démarrage

```
git clone https://github.com/floflowilwil-cpu/Keyce-code.git
cd Keyce-code
dotnet restore
dotnet build
dotnet test
dotnet run --project src/App.Desktop
```

## Répartition de l'équipe (5 développeurs)

| Développeur | Domaine |
|---|---|
| 1 | Interface graphique et expérience utilisateur |
| 2 | Moteur d'édition et documents |
| 3 | Langages, IntelliSense et analyse (LSP) |
| 4 | Extensions, Git et intégrations |
| 5 | Système, terminal, exécution et débogage (DAP) |

## Organisation Git

- Une branche par fonctionnalité : `feature/editor-selection`, `feature/git-status`, etc.
- Ne jamais pousser directement sur `main` : passer par une Pull Request.
- Chaque PR doit compiler, inclure les tests utiles et être relue par un autre membre.
- Les noms, conventions, formats et interfaces publiques sont validés collectivement.
- Utiliser des tickets et signaler rapidement les blocages.

## Plan de réalisation

| Étape | Livrable |
|---|---|
| 0 — Cadrage | Maquettes, architecture, dépôt, conventions et contrats |
| 1 — Squelette | Fenêtre Avalonia, ouverture d'un dossier, explorateur et fichier |
| 2 — Éditeur MVP | Saisie, sélection, sauvegarde, onglets, annuler/rétablir, recherche |
| 3 — Langages | Coloration, LSP, diagnostics, complétion et navigation |
| 4 — Outils | Terminal, exécution, Git, tâches et débogage initial |
| 5 — Extensions | Installation/activation d'extensions et API initiale |
| 6 — Stabilisation | Tests, performances, sécurité, documentation et installation |

## Priorités du MVP

- Fenêtre principale avec explorateur, onglets et zone d'édition
- Créer, ouvrir, modifier et sauvegarder des fichiers
- Coloration syntaxique et au moins un langage via LSP
- Recherche dans le fichier et dans le dossier de travail
- Terminal intégré avec lancement d'un shell local
- Git de base : statut, diff et commit
- Paramètres simples, thèmes et gestion des erreurs

**Reporté après le MVP :** marketplace complète, synchronisation des paramètres, collaboration en direct, édition distante/SSH avancée, compatibilité totale avec les extensions VS Code, assistance IA avancée.

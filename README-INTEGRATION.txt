PATCH 04 — COMMANDES AVANCÉES SUR LES LIGNES

Fichiers à copier :
- src/App.Editor/Editing/EditorLineCommandService.cs
- tests/App.Editor.Tests/EditorLineCommandServiceTests.cs

Ce patch ajoute un service dédié :
- CommentLines(documentId, prefix = "//")
- UncommentLines(documentId, prefix = "//")
- DuplicateLines(documentId)
- MoveLinesUp(documentId)
- MoveLinesDown(documentId)

Intégration :
1. Copier les deux fichiers aux chemins indiqués.
2. Vérifier que App.Editor.Tests référence App.Editor et App.Core comme les tests existants.
3. Exécuter : dotnet test
4. Envoyer le résultat complet avant de poursuivre.

Important :
- Le service est séparé d'EditorCommandService pour ne pas casser son API existante.
- Les modifications passent par IEditorService.ApplyEdits, donc elles utilisent l'historique du document.
- Les déplacements de lignes remplacent le contenu complet du document en une opération Undo.

PATCH 03 — COMMANDES D'ÉDITION

Prérequis :
- Patch 01 (Selection / Caret / CaretSet) installé.
- Patch 02 (ApplyEdits + stockage du CaretSet) installé.
- Les 65 tests précédents sont verts.

Fichiers :
1. Ajouter src/App.Editor/Editing/EditorCommandService.cs
2. Ajouter tests/App.Editor.Tests/EditorCommandServiceTests.cs

Aucun fichier existant n'est remplacé.

Commandes livrées :
- TypeText : insertion/remplacement sur un ou plusieurs curseurs.
- Backspace : suppression de la sélection ou du caractère précédent.
- DeleteForward : suppression de la sélection ou du caractère suivant.
- InsertNewLine : saut de ligne avec reprise de l'indentation.
- Indent : insertion d'une tabulation (ou d'une chaîne d'indentation donnée).
- Outdent : retrait d'une tabulation ou jusqu'à quatre espaces au début de chaque ligne.

Toutes les modifications passent par IEditorService.ApplyEdits : un Undo restaure
l'ensemble de la commande. Les positions sont calculées en UTF-16, conformément
au TextModel existant.

Validation depuis la racine :
dotnet test tests/App.Editor.Tests/App.Editor.Tests.csproj

Ne pas committer avant d'avoir obtenu le résultat des tests.

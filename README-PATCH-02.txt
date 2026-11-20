PATCH 02 — INTEGRATION DU MULTI-CURSEUR ET DES EDITIONS ATOMIQUES

Remplacer les fichiers existants aux chemins indiqués :
- src/App.Core/Contracts/IEditorService.cs
- src/App.Editor/Documents/Document.cs
- src/App.Editor/EditorService.cs

Ajouter :
- tests/App.Editor.Tests/EditorBatchTests.cs

Fonctions ajoutées :
- IEditorService.ApplyEdits(DocumentId, IReadOnlyList<TextEdit>)
- IEditorService.GetCaretSet(DocumentId)
- IEditorService.SetCaretSet(DocumentId, CaretSet)
- Document.ApplyBatch(...)
- Un lot de modifications = un seul groupe Undo/Redo.
- Curseurs conservés par document ouvert et supprimés à la fermeture.
- Validation des positions contre le TextModel.

IMPORTANT :
1. Faire ce patch uniquement sur feature/editor-selection.
2. Les trois fichiers existants sont remplacés : conserver une copie/commit avant installation.
3. Vérifier que le patch 01 (SelectionModels.cs) est déjà installé.
4. Exécuter :
   dotnet test tests/App.Editor.Tests/App.Editor.Tests.csproj

Convention d'événement :
ApplyBatch calcule toutes les plages dans l'état initial, puis les applique
par offsets décroissants. DocumentChanged reçoit l'ordre réel d'application.
Cela permet aux consommateurs incrémentaux (dont LSP) de rejouer les edits
séquentiellement sans décalages erronés.

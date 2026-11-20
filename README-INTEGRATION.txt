KEYCE CODE — PATCH 1 : MODELES DE SELECTION ET MULTI-CURSEURS

Contenu
-------
1. src/App.Core/Models/SelectionModels.cs
   - Selection : ancre, extrémité active, sens et plage normalisée.
   - Caret : position active et ancre de sélection.
   - CaretSet : ensemble immuable, curseur principal, ajout/suppression.

2. tests/App.Editor.Tests/SelectionModelsTests.cs
   - Tests xUnit des invariants et opérations de base.

Installation
------------
Copier les deux fichiers dans les chemins correspondants du dépôt.
Aucune modification de .csproj n'est nécessaire : les projets SDK incluent
automatiquement les fichiers .cs du dossier.

Depuis la racine du dépôt :
    dotnet test tests/App.Editor.Tests/App.Editor.Tests.csproj

Conventions
-----------
- Positions en base 0, colonnes UTF-16, cohérentes avec TextModel et LSP.
- L'ordre des curseurs est conservé ; PrimaryIndex identifie le curseur principal.
- Les positions actives dupliquées sont refusées pour éviter une double édition.
- Les sélections qui se chevauchent sont refusées ; celles qui se touchent sont permises.
- CaretSet est immuable : chaque opération retourne un nouvel ensemble.
- Les positions sont validées pour être non négatives, mais leurs limites par rapport
  au document doivent être vérifiées par le service qui connaît le TextModel.

Important
---------
Ce patch constitue la première couche de modèles. Il n'altère pas encore
IEditorService, Document, Undo/Redo ni le moteur TextBuffer.
La prochaine étape sera l'intégration de CaretSet au niveau du service et
l'application atomique de plusieurs TextEdit dans un seul groupe Undo.

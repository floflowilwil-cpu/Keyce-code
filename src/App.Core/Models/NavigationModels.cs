namespace App.Core.Models;

/// <summary>Déplacements du curseur (flèches, Début/Fin, Ctrl+flèches, Page précédente/suivante).</summary>
public enum CaretMove
{
    Left,
    Right,
    Up,
    Down,

    /// <summary>Début de ligne « intelligent » : premier caractère non blanc, puis colonne 0, en alternance.</summary>
    LineStart,
    LineEnd,
    DocumentStart,
    DocumentEnd,
    WordLeft,
    WordRight,
    PageUp,
    PageDown
}

namespace App.Core.Models;

/// <summary>
/// Encodage d'un fichier texte. Name identifie l'encodage ; HasBom indique si le fichier commence par un BOM
/// (marque d'ordre des octets) qui doit être conservé à l'enregistrement.
/// </summary>
public sealed record FileEncoding(string Name, bool HasBom)
{
    public static readonly FileEncoding Utf8 = new("utf-8", false);
    public static readonly FileEncoding Utf8Bom = new("utf-8", true);
    public static readonly FileEncoding Utf16Le = new("utf-16le", true);
    public static readonly FileEncoding Utf16Be = new("utf-16be", true);
    public static readonly FileEncoding Windows1252 = new("windows-1252", false);
    public static readonly FileEncoding Latin1 = new("iso-8859-1", false);

    /// <summary>Encodages pris en charge (pour le sélecteur de la barre d'état).</summary>
    public static IReadOnlyList<FileEncoding> Supported { get; } =
        [Utf8, Utf8Bom, Utf16Le, Utf16Be, Windows1252, Latin1];

    /// <summary>Libellé lisible, ex. « UTF-8 with BOM ».</summary>
    public string DisplayName => (Name, HasBom) switch
    {
        ("utf-8", false) => "UTF-8",
        ("utf-8", true) => "UTF-8 with BOM",
        ("utf-16le", _) => "UTF-16 LE",
        ("utf-16be", _) => "UTF-16 BE",
        ("windows-1252", _) => "Windows 1252",
        ("iso-8859-1", _) => "Latin-1 (ISO 8859-1)",
        _ => Name
    };
}

/// <summary>Texte d'un fichier et encodage détecté à la lecture (le BOM éventuel est retiré du texte).</summary>
public sealed record FileContent(string Text, FileEncoding Encoding);

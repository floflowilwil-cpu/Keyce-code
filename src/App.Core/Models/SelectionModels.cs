namespace App.Core.Models;

/// <summary>
/// Sélection orientée : Anchor est le point fixe et Active est l'extrémité
/// déplacée par l'utilisateur. Les positions sont en base 0 et en unités UTF-16.
/// </summary>
public readonly record struct Selection(TextPosition Anchor, TextPosition Active)
{
    public bool IsEmpty => Anchor == Active;

    /// <summary>Plage normalisée, indépendante du sens de sélection.</summary>
    public TextRange Range => Compare(Anchor, Active) <= 0
        ? new TextRange(Anchor, Active)
        : new TextRange(Active, Anchor);

    public bool IsReversed => Compare(Anchor, Active) > 0;

    public static Selection Caret(TextPosition position) => new(position, position);

    public Selection CollapseToAnchor() => Caret(Anchor);

    public Selection CollapseToActive() => Caret(Active);

    public Selection WithActive(TextPosition position) => new(Anchor, position);

    public static int Compare(TextPosition left, TextPosition right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    public static bool operator <(Selection left, Selection right) =>
        Compare(left.Anchor, right.Anchor) < 0;

    public static bool operator >(Selection left, Selection right) =>
        Compare(left.Anchor, right.Anchor) > 0;
}

/// <summary>
/// Un curseur d'édition. Anchor == Position représente un curseur sans sélection.
/// Sinon, Anchor et Position définissent une sélection orientée.
/// </summary>
public readonly record struct Caret(TextPosition Position, TextPosition Anchor)
{
    public bool HasSelection => Position != Anchor;

    public Selection Selection => new(Anchor, Position);

    public static Caret At(TextPosition position) => new(position, position);

    public static Caret FromSelection(Selection selection) =>
        new(selection.Active, selection.Anchor);

    public Caret MoveTo(TextPosition position, bool extendSelection = false) =>
        extendSelection
            ? this with { Position = position }
            : At(position);

    public Caret CollapseToActive() => At(Position);
}

/// <summary>
/// Ensemble immuable de curseurs d'un document.
/// Le curseur principal est identifié par PrimaryIndex.
/// L'ordre d'entrée est conservé : les commandes doivent calculer leurs éditions
/// puis les appliquer par offsets décroissants.
/// </summary>
public sealed class CaretSet
{
    private readonly IReadOnlyList<Caret> _carets;

    public CaretSet(IEnumerable<Caret> carets, int primaryIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(carets);

        Caret[] items = carets.ToArray();
        if (items.Length == 0)
            throw new ArgumentException("Un ensemble doit contenir au moins un curseur.", nameof(carets));

        if ((uint)primaryIndex >= (uint)items.Length)
            throw new ArgumentOutOfRangeException(nameof(primaryIndex));

        for (int i = 0; i < items.Length; i++)
        {
            ValidatePosition(items[i].Position, nameof(carets));
            ValidatePosition(items[i].Anchor, nameof(carets));

            for (int j = 0; j < i; j++)
            {
                if (items[i].Position == items[j].Position)
                    throw new ArgumentException("Deux curseurs ne peuvent pas partager la même position active.", nameof(carets));
            }
        }

        EnsureSelectionsDoNotOverlap(items);

        _carets = Array.AsReadOnly(items);
        PrimaryIndex = primaryIndex;
    }

    public IReadOnlyList<Caret> Carets => _carets;

    public int Count => _carets.Count;

    public int PrimaryIndex { get; }

    public Caret Primary => _carets[PrimaryIndex];

    public static CaretSet Single(TextPosition position) =>
        new([Caret.At(position)]);

    public CaretSet WithPrimary(int index) => new(_carets, index);

    public CaretSet Replace(IEnumerable<Caret> carets, int primaryIndex = 0) =>
        new(carets, primaryIndex);

    public CaretSet Add(Caret caret, bool makePrimary = true)
    {
        var items = _carets.ToList();
        items.Add(caret);
        return new CaretSet(items, makePrimary ? items.Count - 1 : PrimaryIndex);
    }

    public CaretSet RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count == 1)
            throw new InvalidOperationException("Le dernier curseur ne peut pas être supprimé.");

        var items = _carets.ToList();
        items.RemoveAt(index);

        int primary = PrimaryIndex;
        if (index < primary) primary--;
        else if (index == primary) primary = Math.Min(index, items.Count - 1);

        return new CaretSet(items, primary);
    }

    private static void ValidatePosition(TextPosition position, string parameterName)
    {
        if (position.Line < 0 || position.Column < 0)
            throw new ArgumentException("Les lignes et colonnes doivent être positives ou nulles.", parameterName);
    }

    private static void EnsureSelectionsDoNotOverlap(IReadOnlyList<Caret> carets)
    {
        var ranges = carets
            .Where(c => c.HasSelection)
            .Select(c => c.Selection.Range)
            .OrderBy(r => r.Start, Comparer<TextPosition>.Create(Selection.Compare))
            .ToArray();

        for (int i = 1; i < ranges.Length; i++)
        {
            // Les plages qui se touchent sont autorisées ; les plages qui se recouvrent ne le sont pas.
            if (Selection.Compare(ranges[i].Start, ranges[i - 1].End) < 0)
                throw new ArgumentException("Les sélections de curseurs différents ne doivent pas se chevaucher.", nameof(carets));
        }
    }
}

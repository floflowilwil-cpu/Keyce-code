using System.Text;

namespace App.Editor;

/// <summary>
/// Moteur de stockage de texte basé sur une Piece Table.
/// Le texte original reste intact et les nouvelles insertions
/// sont conservées dans un tampon séparé.
/// </summary>
public sealed class TextBuffer
{
    private enum BufferKind
    {
        Original,
        Add
    }

    private readonly record struct Piece(
        BufferKind Buffer,
        int Start,
        int Length
    );

    private readonly string _original;
    private readonly StringBuilder _add = new();
    private readonly List<Piece> _pieces = new();

    private int _length;

    public TextBuffer(string initialText = "")
    {
        ArgumentNullException.ThrowIfNull(initialText);

        _original = initialText;
        _length = initialText.Length;

        if (_length > 0)
        {
            _pieces.Add(new Piece(
                BufferKind.Original,
                0,
                _length
            ));
        }
    }

    /// <summary>
    /// Nombre de caractères du document.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Indique si le document est vide.
    /// </summary>
    public bool IsEmpty => _length == 0;

    /// <summary>
    /// Retourne tout le contenu du document.
    /// </summary>
    public string GetText()
    {
        return GetText(0, _length);
    }

    /// <summary>
    /// Retourne une portion du document.
    /// </summary>
    public string GetText(int start, int length)
    {
        ValidateRange(start, length);

        if (length == 0)
            return string.Empty;

        var result = new StringBuilder(length);

        int documentPosition = 0;
        int requestedEnd = start + length;

        foreach (Piece piece in _pieces)
        {
            int pieceEnd = documentPosition + piece.Length;

            int overlapStart = Math.Max(
                start,
                documentPosition
            );

            int overlapEnd = Math.Min(
                requestedEnd,
                pieceEnd
            );

            if (overlapStart < overlapEnd)
            {
                int offset = overlapStart - documentPosition;
                int count = overlapEnd - overlapStart;

                if (piece.Buffer == BufferKind.Original)
                {
                    result.Append(
                        _original,
                        piece.Start + offset,
                        count
                    );
                }
                else
                {
                    // Copie uniquement la portion utile, sans convertir tout le tampon d'ajout.
                    result.Append(
                        _add,
                        piece.Start + offset,
                        count
                    );
                }
            }

            documentPosition = pieceEnd;

            if (documentPosition >= requestedEnd)
                break;
        }

        return result.ToString();
    }

    /// <summary>
    /// Insère du texte à une position précise.
    /// </summary>
    public void Insert(int position, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (position < 0 || position > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position)
            );
        }

        if (text.Length == 0)
            return;

        int addStart = _add.Length;

        _add.Append(text);

        var newPiece = new Piece(
            BufferKind.Add,
            addStart,
            text.Length
        );

        InsertPiece(position, newPiece);

        _length += text.Length;

        MergeAdjacentPieces();
    }

    /// <summary>
    /// Supprime une portion du document.
    /// </summary>
    public void Delete(int start, int length)
    {
        ValidateRange(start, length);

        if (length == 0)
            return;

        int deleteEnd = start + length;
        int currentPosition = 0;

        var remainingPieces = new List<Piece>();

        foreach (Piece piece in _pieces)
        {
            int pieceStart = currentPosition;
            int pieceEnd = pieceStart + piece.Length;

            if (pieceStart < start)
            {
                int keepLength = Math.Min(
                    piece.Length,
                    start - pieceStart
                );

                if (keepLength > 0)
                {
                    remainingPieces.Add(new Piece(
                        piece.Buffer,
                        piece.Start,
                        keepLength
                    ));
                }
            }

            if (pieceEnd > deleteEnd)
            {
                int skipLength = Math.Max(
                    0,
                    deleteEnd - pieceStart
                );

                int remainingLength =
                    piece.Length - skipLength;

                if (remainingLength > 0)
                {
                    remainingPieces.Add(new Piece(
                        piece.Buffer,
                        piece.Start + skipLength,
                        remainingLength
                    ));
                }
            }

            currentPosition = pieceEnd;
        }

        _pieces.Clear();
        _pieces.AddRange(remainingPieces);

        _length -= length;

        MergeAdjacentPieces();
    }

    /// <summary>
    /// Remplace une portion du document.
    /// </summary>
    public void Replace(
        int start,
        int length,
        string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);

        ValidateRange(start, length);

        Delete(start, length);
        Insert(start, newText);
    }

    /// <summary>
    /// Insère un morceau à une position donnée.
    /// </summary>
    private void InsertPiece(int position, Piece newPiece)
    {
        if (_pieces.Count == 0)
        {
            _pieces.Add(newPiece);
            return;
        }

        int currentPosition = 0;

        for (int i = 0; i < _pieces.Count; i++)
        {
            Piece piece = _pieces[i];

            int pieceEnd = currentPosition + piece.Length;

            if (position == currentPosition)
            {
                _pieces.Insert(i, newPiece);
                return;
            }

            if (position < pieceEnd)
            {
                int leftLength = position - currentPosition;
                int rightLength = piece.Length - leftLength;

                var leftPiece = new Piece(
                    piece.Buffer,
                    piece.Start,
                    leftLength
                );

                var rightPiece = new Piece(
                    piece.Buffer,
                    piece.Start + leftLength,
                    rightLength
                );

                _pieces[i] = leftPiece;

                _pieces.Insert(i + 1, newPiece);
                _pieces.Insert(i + 2, rightPiece);

                return;
            }

            if (position == pieceEnd)
            {
                _pieces.Insert(i + 1, newPiece);
                return;
            }

            currentPosition = pieceEnd;
        }

        _pieces.Add(newPiece);
    }

    /// <summary>
    /// Fusionne les morceaux contigus du même tampon.
    /// </summary>
    private void MergeAdjacentPieces()
    {
        for (int i = 0; i < _pieces.Count - 1;)
        {
            Piece current = _pieces[i];
            Piece next = _pieces[i + 1];

            bool canMerge =
                current.Buffer == next.Buffer &&
                current.Start + current.Length == next.Start;

            if (canMerge)
            {
                _pieces[i] = new Piece(
                    current.Buffer,
                    current.Start,
                    current.Length + next.Length
                );

                _pieces.RemoveAt(i + 1);
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// Vérifie qu'une plage appartient au document.
    /// </summary>
    private void ValidateRange(int start, int length)
    {
        if (start < 0 || start > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start)
            );
        }

        if (length < 0 || length > _length - start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length)
            );
        }
    }
}
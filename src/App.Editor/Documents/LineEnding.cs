namespace App.Editor.Documents;

public enum LineEnding { Lf, CrLf }

public static class LineEndingDetector
{
    /// <summary>Fin de ligne majoritaire du texte ; à défaut, celle du système.</summary>
    public static LineEnding Detect(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        if (crlf == 0 && lf == 0) return Environment.NewLine == "\r\n" ? LineEnding.CrLf : LineEnding.Lf;
        return crlf > lf ? LineEnding.CrLf : LineEnding.Lf;
    }
}

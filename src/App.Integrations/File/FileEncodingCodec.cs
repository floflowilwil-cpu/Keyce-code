using System.Text;
using App.Core.Errors;
using App.Core.Models;

namespace App.Integrations.Files;

/// <summary>Détection de l'encodage et conversion octets ⇄ texte.</summary>
public static class FileEncodingCodec
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LePreamble = [0xFF, 0xFE];
    private static readonly byte[] Utf16BePreamble = [0xFE, 0xFF];

    static FileEncodingCodec()
    {
        // Nécessaire pour Windows 1252 sous .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static FileContent Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (StartsWith(bytes, Utf8Preamble))
            return new FileContent(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), FileEncoding.Utf8Bom);
        if (StartsWith(bytes, Utf16LePreamble))
            return new FileContent(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), FileEncoding.Utf16Le);
        if (StartsWith(bytes, Utf16BePreamble))
            return new FileContent(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), FileEncoding.Utf16Be);

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return new FileContent(strictUtf8.GetString(bytes), FileEncoding.Utf8);
        }
        catch (DecoderFallbackException)
        {
            return new FileContent(Encoding.GetEncoding(1252).GetString(bytes), FileEncoding.Windows1252);
        }
    }

    /// <summary>
    /// Décode avec un encodage imposé (« Rouvrir avec l'encodage… »). Un BOM qui correspond à l'encodage est retiré
    /// du texte ; les octets invalides deviennent « � ».
    /// </summary>
    public static FileContent DecodeAs(byte[] bytes, FileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(encoding);

        static bool Starts(byte[] data, params byte[] bom) =>
            data.Length >= bom.Length && data.AsSpan(0, bom.Length).SequenceEqual(bom);

        switch (encoding.Name)
        {
            case "utf-8":
            {
                int skip = Starts(bytes, 0xEF, 0xBB, 0xBF) ? 3 : 0;
                return new FileContent(new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip), encoding);
            }
            case "utf-16le":
            {
                int skip = Starts(bytes, 0xFF, 0xFE) ? 2 : 0;
                return new FileContent(Encoding.Unicode.GetString(bytes, skip, bytes.Length - skip), encoding);
            }
            case "utf-16be":
            {
                int skip = Starts(bytes, 0xFE, 0xFF) ? 2 : 0;
                return new FileContent(Encoding.BigEndianUnicode.GetString(bytes, skip, bytes.Length - skip), encoding);
            }
            case "windows-1252":
                return new FileContent(Encoding.GetEncoding(1252).GetString(bytes), encoding);
            case "iso-8859-1":
                return new FileContent(Encoding.Latin1.GetString(bytes), encoding);
            default:
                throw new AppException(ErrorCode.InvalidArgument, $"Encodage non pris en charge : {encoding.Name}");
        }
    }

    public static byte[] Encode(string content, FileEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        try
        {
            switch (encoding.Name)
            {
                case "utf-8":
                    var utf8 = new UTF8Encoding(false).GetBytes(content);
                    return encoding.HasBom ? Concat(Utf8Preamble, utf8) : utf8;

                case "utf-16le":
                    return Concat(Utf16LePreamble, Encoding.Unicode.GetBytes(content));

                case "utf-16be":
                    return Concat(Utf16BePreamble, Encoding.BigEndianUnicode.GetBytes(content));

                case "windows-1252":
                    return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                        .GetBytes(content);

                case "iso-8859-1":
                    return Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                        .GetBytes(content);

                default:
                    throw new AppException(ErrorCode.InvalidArgument, $"Encodage non pris en charge : {encoding.Name}");
            }
        }
        catch (EncoderFallbackException ex)
        {
            throw new AppException(ErrorCode.InvalidArgument,
                $"Le texte contient des caractères que l'encodage {encoding.DisplayName} ne peut pas représenter. " +
                "Choisissez un encodage Unicode (UTF-8) pour enregistrer.", ex);
        }
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

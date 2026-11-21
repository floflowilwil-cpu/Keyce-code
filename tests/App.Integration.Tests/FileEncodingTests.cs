using System.Text;
using App.Core.Errors;
using App.Core.Models;
using App.Integrations.Files;

namespace App.Integration.Tests;

public sealed class FileEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyce-enc-" + Guid.NewGuid().ToString("N"));
    private readonly FileService _service = new();

    public FileEncodingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    private string P(string name) => Path.Combine(_root, name);

    // ---------- Détection ----------

    [Fact]
    public void Decode_PlainUtf8_WithoutBom()
    {
        var result = FileEncodingCodec.Decode(Encoding.UTF8.GetBytes("élève €"));

        Assert.Equal("élève €", result.Text);
        Assert.Equal(FileEncoding.Utf8, result.Encoding);
    }

    [Fact]
    public void Decode_Utf8Bom_IsDetected_AndRemovedFromText()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("abc")];

        var result = FileEncodingCodec.Decode(bytes);

        Assert.Equal("abc", result.Text);
        Assert.Equal(FileEncoding.Utf8Bom, result.Encoding);
    }

    [Fact]
    public void Decode_Utf16LeBom()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("héllo")];

        var result = FileEncodingCodec.Decode(bytes);

        Assert.Equal("héllo", result.Text);
        Assert.Equal(FileEncoding.Utf16Le, result.Encoding);
    }

    [Fact]
    public void Decode_Utf16BeBom()
    {
        byte[] bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("héllo")];

        var result = FileEncodingCodec.Decode(bytes);

        Assert.Equal("héllo", result.Text);
        Assert.Equal(FileEncoding.Utf16Be, result.Encoding);
    }

    [Fact]
    public void Decode_InvalidUtf8_FallsBackToWindows1252()
    {
        // « café » en Windows 1252 : l'octet 0xE9 n'est pas du UTF-8 valide ici.
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9];

        var result = FileEncodingCodec.Decode(bytes);

        Assert.Equal("café", result.Text);
        Assert.Equal(FileEncoding.Windows1252, result.Encoding);
    }

    [Fact]
    public void Decode_Windows1252_EuroSign()
    {
        var result = FileEncodingCodec.Decode([0x80]);
        Assert.Equal("€", result.Text);
    }

    [Fact]
    public void Decode_EmptyFile_IsUtf8()
    {
        var result = FileEncodingCodec.Decode([]);

        Assert.Equal("", result.Text);
        Assert.Equal(FileEncoding.Utf8, result.Encoding);
    }

    // ---------- Écriture ----------

    [Theory]
    [MemberData(nameof(RoundTripEncodings))]
    public void Encode_ThenDecode_GivesTheSameTextAndEncoding(FileEncoding encoding)
    {
        string text = "café\nligne 2";

        var decoded = FileEncodingCodec.Decode(FileEncodingCodec.Encode(text, encoding));

        Assert.Equal(text, decoded.Text);
        Assert.Equal(encoding, decoded.Encoding);
    }

    public static IEnumerable<object[]> RoundTripEncodings() =>
    [
        [FileEncoding.Utf8],
        [FileEncoding.Utf8Bom],
        [FileEncoding.Utf16Le],
        [FileEncoding.Utf16Be],
        [FileEncoding.Windows1252],
    ];

    [Fact]
    public void Encode_Utf8Bom_StartsWithTheBom()
    {
        byte[] bytes = FileEncodingCodec.Encode("a", FileEncoding.Utf8Bom);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, bytes);
    }

    [Fact]
    public void Encode_Utf8_HasNoBom()
    {
        byte[] bytes = FileEncodingCodec.Encode("a", FileEncoding.Utf8);
        Assert.Equal(new byte[] { 0x61 }, bytes);
    }

    [Fact]
    public void Encode_Windows1252_UsesOneByteForAccents()
    {
        byte[] bytes = FileEncodingCodec.Encode("é", FileEncoding.Windows1252);
        Assert.Equal(new byte[] { 0xE9 }, bytes);
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("iso-8859-1")]
    public void Encode_UnrepresentableCharacters_AreRefused_NotReplacedByQuestionMarks(string name)
    {
        var ex = Assert.Throws<AppException>(() =>
            FileEncodingCodec.Encode("ok 😀", new FileEncoding(name, false)));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Encode_UnknownEncoding_IsRefused()
    {
        var ex = Assert.Throws<AppException>(() =>
            FileEncodingCodec.Encode("a", new FileEncoding("klingon", false)));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    // ---------- FileService ----------

    [Fact]
    public async Task FileService_WriteWithBom_ThenReadWithEncoding_KeepsBom()
    {
        await _service.WriteAsync(P("bom.txt"), "abc", FileEncoding.Utf8Bom);

        byte[] raw = await File.ReadAllBytesAsync(P("bom.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw[..3]);

        var content = await _service.ReadWithEncodingAsync(P("bom.txt"));
        Assert.Equal("abc", content.Text);
        Assert.Equal(FileEncoding.Utf8Bom, content.Encoding);
    }

    [Fact]
    public async Task FileService_Latin1File_IsReadWithoutCorruption_AndRewrittenIdentically()
    {
        byte[] original = [0x63, 0x61, 0x66, 0xE9, 0x0A, 0x6F, 0x6B];
        await File.WriteAllBytesAsync(P("latin.txt"), original);

        var content = await _service.ReadWithEncodingAsync(P("latin.txt"));
        await _service.WriteAsync(P("latin.txt"), content.Text, content.Encoding);

        Assert.Equal("café\nok", content.Text);
        Assert.Equal(original, await File.ReadAllBytesAsync(P("latin.txt")));
    }

    [Fact]
    public async Task FileService_WriteUnrepresentableText_LeavesTheFileUntouched()
    {
        await File.WriteAllBytesAsync(P("keep.txt"), [0x61, 0xE9]);

        await Assert.ThrowsAsync<AppException>(() =>
            _service.WriteAsync(P("keep.txt"), "😀", FileEncoding.Windows1252));

        Assert.Equal(new byte[] { 0x61, 0xE9 }, await File.ReadAllBytesAsync(P("keep.txt")));
    }

    [Fact]
    public async Task FileService_ReadWithEncoding_OnMissingFile_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.ReadWithEncodingAsync(P("absent.txt")));
        Assert.Equal(ErrorCode.NotFound, ex.Code);
    }
}

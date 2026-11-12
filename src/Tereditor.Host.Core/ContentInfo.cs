using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Host.Core;

/// <summary>The host's one definition of a resource's identity (SHA-256) and of what counts as text.</summary>
public static class ContentInfo
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Valid UTF-8, with or without a byte order mark. The classifier adds the NUL rule on top.</summary>
    public static bool IsText(ReadOnlySpan<byte> bytes) => DecodesStrictly(bytes);

    private static bool DecodesStrictly(ReadOnlySpan<byte> bytes)
    {
        try { Strict.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>Decodes text without dropping a byte order mark, so re-encoding returns the same bytes.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => Strict.GetString(bytes);

    public static byte[] Encode(string text) => Strict.GetBytes(text);
}
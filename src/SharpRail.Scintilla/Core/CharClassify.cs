namespace SharpRail.Scintilla.Core;

/// <summary>Byte character classes for word boundaries (upstream src/CharClassify.h/.cxx).</summary>
/// <remarks>Classes cover UTF-8 bytes; every byte at or above 0x80 is a word byte by default. Unicode-aware
/// classification of whole characters belongs to the document, which uses .NET's Unicode data.</remarks>
internal sealed class CharClassify
{
    private const int MaxChar = 256;
    private readonly CharacterClass[] charClass = new CharacterClass[MaxChar];

    internal CharClassify() => SetDefaultCharClasses(true);

    internal void SetDefaultCharClasses(bool includeWordClass)
    {
        for (int ch = 0; ch < MaxChar; ch++)
        {
            if (ch is '\r' or '\n')
                charClass[ch] = CharacterClass.NewLine;
            else if (ch < 0x20 || ch == 0x7f || ch == ' ')
                charClass[ch] = CharacterClass.Space;
            else if (includeWordClass && (ch >= 0x80 || char.IsAsciiLetterOrDigit((char)ch) || ch == '_'))
                charClass[ch] = CharacterClass.Word;
            else
                charClass[ch] = CharacterClass.Punctuation;
        }
    }

    internal void SetCharClasses(ReadOnlySpan<byte> chars, CharacterClass newCharClass)
    {
        foreach (byte ch in chars)
        {
            if (ch == 0) break;
            charClass[ch] = newCharClass;
        }
    }

    /// <summary>Bytes in <paramref name="characterClass"/>, highest first as upstream.</summary>
    internal byte[] GetCharsOfClass(CharacterClass characterClass)
    {
        var result = new List<byte>();
        for (int ch = MaxChar - 1; ch >= 0; ch--)
            if (charClass[ch] == characterClass) result.Add((byte)ch);
        return [.. result];
    }

    internal CharacterClass GetClass(byte ch) => charClass[ch];

    internal bool IsWord(byte ch) => charClass[ch] == CharacterClass.Word;
}
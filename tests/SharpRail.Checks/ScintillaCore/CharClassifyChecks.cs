using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testCharClassify.cxx.</summary>
internal static class CharClassifyChecks
{
    private const int ByteValues = 256;

    internal static void Run()
    {
        var pcc = new CharClassify();
        var charClass = DefaultClasses();
        for (int i = 0; i < ByteValues; i++) Expect.Equal(charClass[i], pcc.GetClass((byte)i), $"Defaults {i}");

        pcc = new CharClassify();
        for (int i = 0; i < ByteValues; i++)
        {
            var thisClass = (CharacterClass)(i % 4);
            pcc.SetCharClasses([(byte)i], thisClass);
            charClass[i] = thisClass;
        }
        // Upstream passes a NUL-terminated buffer, so byte 0 is never set and stays at its default.
        charClass[0] = CharacterClass.Space;
        for (int i = 0; i < ByteValues; i++) Expect.Equal(charClass[i], pcc.GetClass((byte)i), $"Custom {i}");

        pcc = new CharClassify();
        charClass = DefaultClasses();
        for (int i = 1; i < ByteValues; i++)
        {
            var thisClass = (CharacterClass)(i % 4);
            pcc.SetCharClasses([(byte)i], thisClass);
            charClass[i] = thisClass;
        }
        for (int classVal = 0; classVal < 4; classVal++)
        {
            var thisClass = (CharacterClass)classVal;
            var chars = pcc.GetCharsOfClass(thisClass);
            for (int i = 1; i < ByteValues; i++)
                Expect.Equal(charClass[i] == thisClass, Array.IndexOf(chars, (byte)i) >= 0, $"CharsOfClass {thisClass} {i}");
        }

        pcc = new CharClassify();
        Expect.True(pcc.IsWord((byte)'a') && pcc.IsWord((byte)'_') && pcc.IsWord(0xC3), "IsWord");
        Expect.True(!pcc.IsWord((byte)'.') && !pcc.IsWord((byte)' '), "IsWord punctuation/space");
        pcc.SetDefaultCharClasses(false);
        Expect.Equal(CharacterClass.Punctuation, pcc.GetClass((byte)'a'), "Defaults without word class");
    }

    private static CharacterClass[] DefaultClasses()
    {
        var charClass = new CharacterClass[ByteValues];
        for (int ch = 0; ch < ByteValues; ch++)
        {
            if (ch is '\r' or '\n') charClass[ch] = CharacterClass.NewLine;
            else if (ch < 0x20 || ch == ' ' || ch == 0x7f) charClass[ch] = CharacterClass.Space;
            else if (ch >= 0x80 || char.IsAsciiLetterOrDigit((char)ch) || ch == '_') charClass[ch] = CharacterClass.Word;
            else charClass[ch] = CharacterClass.Punctuation;
        }
        return charClass;
    }
}
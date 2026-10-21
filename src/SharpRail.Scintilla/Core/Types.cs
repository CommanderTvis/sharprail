// Shared contract types for the managed Scintilla core. Owned by the skeleton; change only by agreement.
namespace SharpRail.Scintilla.Core;

/// <summary>Line ending inserted by Enter and used to normalise pasted text (SC_EOL_*).</summary>
internal enum EndOfLine { CrLf = 0, Cr = 1, Lf = 2 }

/// <summary>Character class for word movement and selection (Scintilla's CharacterClass).</summary>
internal enum CharacterClass { Space, NewLine, Word, Punctuation }

/// <summary>Kinds of document change reported to watchers (subset of SC_MOD_*).</summary>
[Flags]
internal enum ModificationFlags
{
    None = 0, InsertText = 0x1, DeleteText = 0x2, ChangeStyle = 0x4, User = 0x10, Undo = 0x20, Redo = 0x40,
    MultiStepUndoRedo = 0x80, LastStepInUndoRedo = 0x100, ChangeMargin = 0x10000, ChangeIndicator = 0x4000,
    BeforeInsert = 0x400, BeforeDelete = 0x800,
}

/// <summary>A document change: positions are UTF-8 byte offsets; <see cref="LinesAdded"/> may be negative.</summary>
internal readonly record struct DocumentChange(ModificationFlags Flags, int Position, int Length, int LinesAdded);

/// <summary>Receives document notifications; the editor implements it to keep layout, wrap and selection in step.</summary>
internal interface IDocumentWatcher
{
    void Modified(DocumentChange change);
    void SavePointChanged(bool atSavePoint);
    void StyleNeeded(int endPosition) { }
}
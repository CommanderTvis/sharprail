namespace SharpRail.Scintilla;

/// <summary>A selection as one-based lines and columns counted in UTF-16 units; <see cref="Text"/> is empty when it is collapsed.</summary>
public readonly record struct ScintillaSelection(int StartLine, int StartColumn, int EndLine, int EndColumn, string Text);
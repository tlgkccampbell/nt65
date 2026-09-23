namespace Norristown.Semantics;

/// <summary>
/// A name one file writes that the file declaring it does not export, identified by the file
/// that declares it and the qualified name it is declared under. It is a name rather than the symbol
/// because the file that wrote it may not be read again when the file that declares it is, and
/// the name is what survives that.
/// </summary>
/// <param name="Path">The file that declares it.</param>
/// <param name="QualifiedName">What it is called in that file.</param>
internal readonly record struct UnexportedName(string Path, string QualifiedName);

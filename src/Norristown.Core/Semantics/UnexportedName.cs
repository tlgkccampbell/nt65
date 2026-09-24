namespace Norristown.Semantics;

/// <summary>
/// Represents a name one file uses that the declaring file does not export, identified by the
/// declaring file and the qualified name it is declared under. It is a name rather than the
/// symbol, because the file that uses it may not be read again when the declaring file is, and
/// only the name stays valid across that.
/// </summary>
/// <param name="Path">The file that declares the name.</param>
/// <param name="QualifiedName">The name's qualified name in that file.</param>
internal readonly record struct UnexportedName(string Path, string QualifiedName);

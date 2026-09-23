namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A diagnostic on a green node or token, placed within that node rather than in the file: an
/// offset from where the node starts, trivia included, and a width. A green node has no position,
/// so its diagnostics cannot have one either, and that is what lets a line keep them across an
/// edit anywhere else in the file. The offset may be negative: a missing token uses that to point
/// back past the trivia in front of it, to the end of the last token the source actually wrote.
/// <para>
/// Every diagnostic the lexer and the parser make is an error, so there is no severity to carry.
/// </para>
/// </summary>
/// <param name="Offset">Where it starts, from the node's own start.</param>
/// <param name="Width">How many characters it covers; 0 for a caret between two of them.</param>
/// <param name="Message">What to tell the programmer, and the catalogue name it is reported under.</param>
/// <param name="Fix">The change the message names as its fix, or null.</param>
internal sealed record GreenDiagnostic(int Offset, int Width, DiagnosticMessage Message, DiagnosticFix? Fix = null);

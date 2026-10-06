using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>Represents one fact about a hazard on a <see cref="DirectPage"/>, for an editor to list.</summary>
/// <param name="Glyph">The glyph the editor shows before the text, such as <c>⚠</c> or <c>↳</c>.</param>
/// <param name="Text">A short phrase, with code in backticks, such as <c>live across `jsr plot`</c>.</param>
/// <param name="At">The statement the fact is about, or null when it is about no one line.</param>
public sealed record PageNote(string Glyph, string Text, SyntaxNode? At);

using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// One change to one file: the text to write over a range of it. Edits are worked out against
/// the files as they are now, so an editor that has moved on sends the request again rather
/// than applying something stale.
/// </summary>
/// <param name="Tree">The file to change.</param>
/// <param name="Span">What to write over, which may be empty for an insertion.</param>
/// <param name="Text">What to write there.</param>
internal readonly record struct Edit(SyntaxTree Tree, TextSpan Span, string Text);

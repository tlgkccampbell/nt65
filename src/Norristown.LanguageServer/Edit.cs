using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents one change to one file, which replaces a range of the file with new text. Edits
/// are computed against the files as they are now, so an editor that has moved on sends the
/// request again rather than applying a stale edit.
/// </summary>
/// <param name="Tree">The file to change.</param>
/// <param name="Span">The range to replace, which may be empty for an insertion.</param>
/// <param name="Text">The text to put in its place.</param>
internal readonly record struct Edit(SyntaxTree Tree, TextSpan Span, string Text);

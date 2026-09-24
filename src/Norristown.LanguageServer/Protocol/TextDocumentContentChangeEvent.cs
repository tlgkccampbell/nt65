namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents one edit, in which <see cref="Text"/> replaces <see cref="Range"/>, or replaces the
/// whole document when the range is null.
/// </summary>
/// <param name="Range">The range the text replaces, or null for the whole document.</param>
/// <param name="Text">The new text.</param>
internal sealed record TextDocumentContentChangeEvent(Range? Range, string Text);

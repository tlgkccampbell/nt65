namespace Norristown.LanguageServer.Protocol;

/// <summary>One edit: <see cref="Text"/> replaces <see cref="Range"/>, or the whole document when it is null.</summary>
/// <param name="Range">What the text replaces.</param>
/// <param name="Text">The new text.</param>
internal sealed record TextDocumentContentChangeEvent(Range? Range, string Text);

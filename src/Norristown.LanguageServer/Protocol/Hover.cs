namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents the information shown about the item under the pointer.</summary>
/// <param name="Contents">The text to show.</param>
/// <param name="Range">The range the text is about, which the client highlights.</param>
internal sealed record Hover(MarkupContent Contents, Range? Range);

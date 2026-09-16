namespace Norristown.LanguageServer.Protocol;

/// <summary>What to show about the thing under the pointer.</summary>
/// <param name="Contents">The text.</param>
/// <param name="Range">What the text is about, which the client highlights.</param>
internal sealed record Hover(MarkupContent Contents, Range? Range);

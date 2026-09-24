namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a range the client highlights when the caret is on a name.</summary>
/// <param name="Range">The range.</param>
/// <param name="Kind">How the name is used there.</param>
internal sealed record DocumentHighlight(Range Range, DocumentHighlightKind Kind);

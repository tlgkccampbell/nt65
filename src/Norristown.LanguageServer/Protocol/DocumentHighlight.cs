namespace Norristown.LanguageServer.Protocol;

/// <summary>One place a client marks when the caret is on a name.</summary>
/// <param name="Range">Where it is.</param>
/// <param name="Kind">How the name is used there.</param>
internal sealed record DocumentHighlight(Range Range, DocumentHighlightKind Kind);

namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one fact about a hazard.</summary>
/// <param name="Glyph">The glyph shown before the text, such as <c>⚠</c>.</param>
/// <param name="Text">A short phrase, with code in backticks.</param>
/// <param name="Place">The line the fact is about, or null.</param>
internal sealed record DirectPageNote(string Glyph, string Text, Location? Place);

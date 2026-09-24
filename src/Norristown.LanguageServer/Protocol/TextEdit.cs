namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one replacement in a document.</summary>
/// <param name="Range">The range to replace.</param>
/// <param name="NewText">The text to put in its place.</param>
internal sealed record TextEdit(Range Range, string NewText);

namespace Norristown.LanguageServer.Protocol;

/// <summary>One replacement in a document.</summary>
/// <param name="Range">What to replace.</param>
/// <param name="NewText">What to put there.</param>
internal sealed record TextEdit(Range Range, string NewText);

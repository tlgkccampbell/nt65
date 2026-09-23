namespace Norristown.LanguageServer.Protocol;

/// <summary>A line of text the client shows above a line of the document.</summary>
/// <param name="Range">What it is about; the client shows it above that range's first line.</param>
/// <param name="Command">
/// The lens text (the command's title) and what clicking it runs. A purely informational lens
/// has an empty command name, and the client shows it without making it clickable.
/// </param>
internal sealed record CodeLens(Range Range, Command Command);

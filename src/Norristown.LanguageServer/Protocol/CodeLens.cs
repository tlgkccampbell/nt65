namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a line of text the client shows above a line of the document.</summary>
/// <param name="Range">
/// The range the lens is about. The client shows the lens above the range's first line.
/// </param>
/// <param name="Command">
/// The command whose title is the lens text and which runs when the lens is clicked. A purely
/// informational lens has an empty command name, and the client shows it without making it
/// clickable.
/// </param>
internal sealed record CodeLens(Range Range, Command Command);

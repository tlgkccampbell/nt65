namespace Norristown.LanguageServer.Protocol;

/// <summary>A line of text the client shows above a line of the document.</summary>
/// <param name="Range">What it is about; the client shows it above that range's first line.</param>
/// <param name="Command">
/// What it says, and what clicking it runs. A lens that only tells you something names no
/// command, and the client shows it without making it clickable.
/// </param>
internal sealed record CodeLens(Range Range, Command Command);

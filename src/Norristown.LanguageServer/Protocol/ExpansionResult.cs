namespace Norristown.LanguageServer.Protocol;

/// <summary>A macro call written out as the nt65 the programmer would have written by hand.</summary>
/// <param name="Title">The call as it stands, which is what names the view.</param>
/// <param name="Summary">The one line saying what it becomes: lines, bytes and what it costs.</param>
/// <param name="Text">The expansion, with <c>\n</c> line endings.</param>
/// <param name="Links">The calls left as calls, each with the way to ask for it one level down.</param>
/// <param name="Note">Why the expansion is not something to write into a file, or null.</param>
internal sealed record ExpansionResult(
    string Title, string Summary, string Text, IReadOnlyList<ExpansionLink> Links, string? Note);

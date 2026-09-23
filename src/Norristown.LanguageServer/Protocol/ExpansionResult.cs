namespace Norristown.LanguageServer.Protocol;

/// <summary>A macro call written out as the nt65 the programmer would have written by hand.</summary>
/// <param name="Title">The call as written, used as the view's title.</param>
/// <param name="Summary">A one-line summary of what it expands to: lines, bytes and cycles.</param>
/// <param name="Text">The expansion, with <c>\n</c> line endings.</param>
/// <param name="Links">The calls left unexpanded, each with how to ask for its expansion one level down.</param>
/// <param name="Note">Why the expansion cannot be written into the file in place of the call, or null.</param>
internal sealed record ExpansionResult(
    string Title, string Summary, string Text, IReadOnlyList<ExpansionLink> Links, string? Note);

namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a macro call expanded into the nt65 source the programmer would have written by hand.
/// </summary>
/// <param name="Title">The call as written, used as the view's title.</param>
/// <param name="Summary">A one-line summary of the expansion's lines, bytes and cycles.</param>
/// <param name="Text">The expansion, with <c>\n</c> line endings.</param>
/// <param name="Links">
/// The calls left unexpanded, each with the value that requests its expansion one level down.
/// </param>
/// <param name="Note">The reason the expansion cannot replace the call in the file, or null.</param>
internal sealed record ExpansionResult(
    string Title, string Summary, string Text, IReadOnlyList<ExpansionLink> Links, string? Note);

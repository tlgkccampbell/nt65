namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents what the server needs to find the edits of a code action it sent without them. The
/// client sends it back unchanged in <c>codeAction/resolve</c>, and the server asks again what can
/// be done where the action was offered.
/// </summary>
/// <param name="Uri">The document the action was offered in.</param>
/// <param name="Range">The range the client asked about.</param>
/// <param name="Only">The kinds of action the client asked for, or null for all kinds.</param>
/// <param name="Index">Where the action was in the list the server sent.</param>
internal sealed record CodeActionData(string Uri, Range Range, IReadOnlyList<string>? Only, int Index);

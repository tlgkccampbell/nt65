namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides renames.</summary>
/// <param name="PrepareProvider">
/// Whether the server answers <c>textDocument/prepareRename</c>, so that a client can find out
/// what it is about to rename before requesting the edits.
/// </param>
internal sealed record RenameOptions(bool PrepareProvider);

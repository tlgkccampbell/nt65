namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server does renames.</summary>
/// <param name="PrepareProvider">
/// Whether it answers <c>textDocument/prepareRename</c>, so a client can find out what it is
/// about to rename before asking for the edits.
/// </param>
internal sealed record RenameOptions(bool PrepareProvider);

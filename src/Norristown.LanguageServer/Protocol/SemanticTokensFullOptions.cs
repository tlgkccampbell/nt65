namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides tokens for a whole document.</summary>
/// <param name="Delta">
/// Whether the server can return the changes since the result the client holds, rather than every
/// number again. A long file is thousands of numbers, and a keystroke moves a handful of them.
/// </param>
internal sealed record SemanticTokensFullOptions(bool Delta);

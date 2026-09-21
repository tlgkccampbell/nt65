namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server answers about a whole document.</summary>
/// <param name="Delta">
/// Whether it can answer what changed since the answer the client is holding, rather than every
/// number again. A long file is thousands of numbers, and a keystroke moves a handful of them.
/// </param>
internal sealed record SemanticTokensFullOptions(bool Delta);

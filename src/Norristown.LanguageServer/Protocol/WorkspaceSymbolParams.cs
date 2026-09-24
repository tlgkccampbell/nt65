namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>workspace/symbol</c> request.</summary>
/// <param name="Query">The text the programmer typed to find a symbol by.</param>
internal sealed record WorkspaceSymbolParams(string Query);

namespace Norristown.LanguageServer.Protocol;

/// <summary>What the programmer typed to find a symbol by.</summary>
internal sealed record WorkspaceSymbolParams(string Query);

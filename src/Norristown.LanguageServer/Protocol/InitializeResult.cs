namespace Norristown.LanguageServer.Protocol;

internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

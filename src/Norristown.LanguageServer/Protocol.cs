using System.Text.Json;

namespace Norristown.LanguageServer;

// Hand-written LSP types, only for the messages the server handles. Add to them as stages
// need more of the protocol. Property names are camel-cased by the formatter.

internal sealed record ClientInfo(string Name, string? Version);

internal sealed record InitializeParams(ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities);

internal sealed record ServerInfo(string Name, string Version);

internal sealed record ServerCapabilities;

internal sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

internal enum MessageType { Error = 1, Warning = 2, Info = 3, Log = 4 }

internal sealed record LogMessageParams(MessageType Type, string Message);

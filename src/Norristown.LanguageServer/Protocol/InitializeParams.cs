using System.Text.Json;

namespace Norristown.LanguageServer.Protocol;

internal sealed record InitializeParams(ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities);

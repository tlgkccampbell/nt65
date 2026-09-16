using System.Text.Json;

namespace Norristown.LanguageServer.Protocol;

// RootUri is the folder the client opened, which is where nt65.json is looked for (§5.3).
internal sealed record InitializeParams(
    ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities, string? RootUri);

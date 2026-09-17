using System.Text.Json;

namespace Norristown.LanguageServer.Protocol;

// RootUri is the folder the client opened, which is where nt65.json is looked for.
// InitializationOptions are the client's `nt65` settings, such as the active configuration.
internal sealed record InitializeParams(
    ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities, string? RootUri,
    JsonElement? InitializationOptions = null);

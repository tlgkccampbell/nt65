using System.Text.Json;

namespace Norristown.LanguageServer.Protocol;

// RootUri is the folder the client opened, and WorkspaceFolders every folder when it opened more
// than one, which is where nt65.json files are looked for. InitializationOptions are the client's
// `nt65` settings, such as the active configuration.
internal sealed record InitializeParams(
    ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities, string? RootUri,
    JsonElement? InitializationOptions = null, IReadOnlyList<WorkspaceFolder>? WorkspaceFolders = null);

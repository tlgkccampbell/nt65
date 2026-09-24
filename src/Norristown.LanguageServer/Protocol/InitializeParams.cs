using System.Text.Json;

namespace Norristown.LanguageServer.Protocol;

/// <summary>Parameters of the <c>initialize</c> request.</summary>
/// <param name="ClientInfo">The client's name and version, or null.</param>
/// <param name="ProcessId">The client's process id, or null.</param>
/// <param name="Capabilities">The client's capabilities, as raw JSON.</param>
/// <param name="RootUri">The folder the client opened, where <c>nt65.json</c> files are looked for.</param>
/// <param name="InitializationOptions">
/// The client's <c>nt65</c> settings, such as the active configuration.
/// </param>
/// <param name="WorkspaceFolders">
/// Every folder the client opened, when it opened more than one. <c>nt65.json</c> files are looked
/// for in each of them.
/// </param>
internal sealed record InitializeParams(
    ClientInfo? ClientInfo, int? ProcessId, JsonElement Capabilities, string? RootUri,
    JsonElement? InitializationOptions = null, IReadOnlyList<WorkspaceFolder>? WorkspaceFolders = null);

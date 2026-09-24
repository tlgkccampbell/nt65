using System.Text.Json.Serialization;

namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents a command for the client to run after it applies a completion or a code action, or
/// when a code lens is clicked.
/// </summary>
/// <param name="Title">
/// The command's title. A code lens displays it as the lens text; after a completion or a code
/// action it is not shown.
/// </param>
/// <param name="Name">The command's identifier, as the client knows it.</param>
/// <param name="Arguments">
/// The arguments to run the command with, or null for a command that takes none.
/// </param>
internal sealed record Command(
    string Title,
    [property: JsonPropertyName("command")] string Name,
    IReadOnlyList<object>? Arguments = null);

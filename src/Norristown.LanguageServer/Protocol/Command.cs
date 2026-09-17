using System.Text.Json.Serialization;

namespace Norristown.LanguageServer.Protocol;

/// <summary>Something the client runs once it has applied a completion or a code action.</summary>
/// <param name="Title">What the command is called, which nothing here shows.</param>
/// <param name="Name">The command, as the client knows it.</param>
/// <param name="Arguments">What it is run with, or null for a command that takes nothing.</param>
internal sealed record Command(
    string Title,
    [property: JsonPropertyName("command")] string Name,
    IReadOnlyList<object>? Arguments = null);

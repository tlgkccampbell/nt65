using System.Text.Json.Serialization;

namespace Norristown.LanguageServer.Protocol;

/// <summary>Something the client runs once it has applied a completion.</summary>
/// <param name="Title">What the command is called, which nothing here shows.</param>
/// <param name="Name">The command, as the client knows it.</param>
internal sealed record Command(string Title, [property: JsonPropertyName("command")] string Name);
